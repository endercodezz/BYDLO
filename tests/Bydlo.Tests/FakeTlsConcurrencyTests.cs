using Bydlo.Engines.TgWsProxy;

namespace Bydlo.Tests;

/// <summary>FakeTlsStream is read and written by two bridge workers at the same time.</summary>
public sealed class FakeTlsConcurrencyTests
{
    /// <summary>Serves scripted bytes to reads; holds every write until released and records it.</summary>
    private sealed class GatedStream(byte[] incoming) : Stream
    {
        private int _offset;
        public TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MemoryStream Written { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = Math.Min(buffer.Length, incoming.Length - _offset);
            incoming.AsSpan(_offset, n).CopyTo(buffer.Span);
            _offset += n;
            return ValueTask.FromResult(n);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteEntered.TrySetResult();
            await ReleaseWrite.Task.WaitAsync(cancellationToken);
            // Copy only now: a buffer reused by a concurrent read would show up here.
            Written.Write(buffer.Span);
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Skipping_a_change_cipher_spec_does_not_corrupt_a_pending_write()
    {
        // Incoming: a 200-byte CCS record (0xEE), then an ApplicationData record "ok".
        var ccs = new byte[200];
        ccs.AsSpan().Fill(0xEE);
        byte[] incoming = [0x14, 0x03, 0x03, 0x00, 200, .. ccs, 0x17, 0x03, 0x03, 0x00, 0x02, (byte)'o', (byte)'k'];
        var inner = new GatedStream(incoming);
        var tls = new FakeTlsStream(inner);

        var payload = new byte[300];
        Random.Shared.NextBytes(payload);
        var write = tls.WriteAsync(payload.AsMemory()).AsTask();
        await inner.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The write is parked inside the inner stream while the reader skips the CCS payload.
        var buffer = new byte[16];
        var read = await tls.ReadAsync(buffer.AsMemory()).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("ok"u8.ToArray(), buffer[..read]);

        inner.ReleaseWrite.SetResult();
        await write.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(FakeTlsStream.Wrap(payload), inner.Written.ToArray());
    }
}
