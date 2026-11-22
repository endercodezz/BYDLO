using System.Buffers.Binary;
using Bydlo.Engines.TgWsProxy;

namespace Bydlo.Tests;

/// <summary>Allocation-free upstream path (issue #359); wire bytes are unchanged.</summary>
[Collection("AllocationMeasurements")]
public sealed class TgWsProxyAllocationTests
{
    /// <summary>Answers the upgrade once (optionally with trailing frame bytes), then idles; writes are recorded or discarded.</summary>
    private sealed class ScriptedStream(byte[] response, bool record) : Stream
    {
        private int _offset;
        public MemoryStream Written { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < response.Length)
            {
                var n = Math.Min(buffer.Length, response.Length - _offset);
                response.AsSpan(_offset, n).CopyTo(buffer.Span);
                _offset += n;
                return n;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (record) { Written.Write(buffer.Span); }
            return ValueTask.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static readonly byte[] Upgrade = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n\r\n"u8.ToArray();

    private static byte[] Packet(int payload, byte fill)
    {
        var packet = new byte[4 + payload];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)payload);
        packet.AsSpan(4).Fill(fill);
        return packet;
    }

    /// <summary>Decodes masked client frames back to payloads.</summary>
    private static List<byte[]> Unframe(ReadOnlySpan<byte> wire)
    {
        var payloads = new List<byte[]>();
        while (!wire.IsEmpty)
        {
            Assert.Equal(0x82, wire[0]);
            Assert.True((wire[1] & 0x80) != 0);
            int length = wire[1] & 0x7F, at = 2;
            if (length == 126) { length = BinaryPrimitives.ReadUInt16BigEndian(wire[2..]); at = 4; }
            else if (length == 127) { length = (int)BinaryPrimitives.ReadUInt64BigEndian(wire[2..]); at = 10; }
            var key = wire.Slice(at, 4);
            var payload = wire.Slice(at + 4, length).ToArray();
            for (var i = 0; i < payload.Length; i++) { payload[i] ^= key[i & 3]; }
            payloads.Add(payload);
            wire = wire[(at + 4 + length)..];
        }

        return payloads;
    }

    [Fact]
    public async Task Frame_bytes_arriving_with_the_upgrade_head_are_not_lost()
    {
        byte[] frame = [0x82, 0x02, (byte)'x', (byte)'y'];
        var stream = new ScriptedStream([.. Upgrade, .. frame], record: false);
        await using var ws = await RawWebSocket.UpgradeAsync(stream, "kws2.web.telegram.org", "/apiws", CancellationToken.None);
        Assert.Equal("xy"u8.ToArray(), await ws.ReceiveAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Packets_go_out_as_one_frame_each_with_the_same_payloads()
    {
        var stream = new ScriptedStream(Upgrade, record: true);
        await using var ws = await RawWebSocket.UpgradeAsync(stream, "kws2.web.telegram.org", "/apiws", CancellationToken.None);
        stream.Written.SetLength(0);
        var splitter = new MsgSplitter(MtProto.Intermediate);
        var lengths = new List<int>();
        byte[][] packets = [Packet(8, 1), Packet(200, 2), Packet(70_000, 3)];
        byte[] chunk = [.. packets.SelectMany(p => p), .. Packet(16, 4).AsSpan(0, 10)];
        var data = splitter.SplitInto(chunk, chunk, lengths);
        Assert.Equal(packets.Select(p => p.Length), lengths);
        await ws.SendPacketsAsync(data, lengths, CancellationToken.None);
        Assert.Equal(packets, Unframe(stream.Written.ToArray()));

        // The partial packet is completed by the next chunk.
        var rest = Packet(16, 4)[10..];
        data = splitter.SplitInto(rest, rest, lengths);
        Assert.Equal([Packet(16, 4)], [data.ToArray()]);
    }

    [Fact]
    public async Task Upstream_path_allocates_under_a_fifth_of_a_byte_per_byte()
    {
        var stream = new ScriptedStream(Upgrade, record: false);
        await using var ws = await RawWebSocket.UpgradeAsync(stream, "kws2.web.telegram.org", "/apiws", CancellationToken.None);
        var splitter = new MsgSplitter(MtProto.Intermediate);
        var lengths = new List<int>();
        // Four packets per 16 KiB read, the last one split across reads.
        byte[] stream16 = [.. Packet(4092, 1), .. Packet(4092, 2), .. Packet(4092, 3), .. Packet(4092, 4)];
        var chunk = stream16.AsMemory(0, 16 * 1024 - 100);
        var tail = stream16.AsMemory(16 * 1024 - 100);

        async Task Round()
        {
            var a = splitter.SplitInto(chunk.Span, chunk.Span, lengths);
            await ws.SendPacketsAsync(a, lengths, CancellationToken.None);
            var b = splitter.SplitInto(tail.Span, tail.Span, lengths);
            await ws.SendPacketsAsync(b, lengths, CancellationToken.None);
        }

        for (var i = 0; i < 50; i++) { await Round(); }
        const int Rounds = 1000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Rounds; i++) { await Round(); }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var traffic = (double)Rounds * stream16.Length;
        Assert.True(allocated / traffic < 0.2, $"{allocated / traffic:0.000} B per byte");
    }

    [Fact]
    public async Task Fake_tls_writes_match_the_reference_wrapping()
    {
        var sink = new MemoryStream();
        var tls = new FakeTlsStream(sink);
        var payload = new byte[40_000];
        Random.Shared.NextBytes(payload);
        await tls.WriteAsync(payload.AsMemory());
        Assert.Equal(FakeTlsStream.Wrap(payload), sink.ToArray());
    }
}
