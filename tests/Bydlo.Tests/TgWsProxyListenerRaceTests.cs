using System.IO;
using System.Net;
using Bydlo.Engines.TgWsProxy;

namespace Bydlo.Tests;

/// <summary>
/// Issue #346: a replacement listener published by the restart path while
/// <see cref="TgWsProxyServer.DisposeAsync"/> is running must still be closed.
/// First listener throws from Accept; the re-bind (Listen) is held back,
/// DisposeAsync is started, then Listen is released. Both listeners must end
/// up disposed so the port does not stay bound. See also #51.
/// </summary>
public sealed class TgWsProxyListenerRaceTests
{
    private static readonly byte[] Secret = Convert.FromHexString("0123456789abcdef0123456789abcdef");

    [Fact]
    public async Task Dispose_during_restart_closes_replacement_listener()
    {
        var network = new RaceNetwork();
        var options = new TgWsProxyOptions { Secret = Secret, PoolSize = 0, CfProxyFallback = false };
        await using var server = new TgWsProxyServer(options, network, (_, _) => { });
        await server.StartAsync();

        Assert.NotNull(network.First);

        // The accept loop fails on the first listener and blocks inside the
        // second Listen call (after the 1s restart delay).
        await network.SecondListenEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var dispose = server.DisposeAsync().AsTask();

        // Let DisposeAsync observe the cancellation, then release the re-bind.
        // The test waits for the first listener to be closed first, mirroring
        // the real scenario where the old socket goes away during shutdown.
        await network.FirstDisposed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        network.ReleaseSecondListen();

        await dispose.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(network.First!.Disposed, "first listener was not disposed");
        Assert.NotNull(network.Second);
        Assert.True(network.Second!.Disposed, "replacement listener leaked: port would stay bound");
    }

    private sealed class RaceNetwork : ITgNetwork
    {
        private int _listens;

        public RaceListener? First { get; private set; }

        public RaceListener? Second { get; private set; }

        public TaskCompletionSource SecondListenEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _releaseSecond = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseSecondListen() => _releaseSecond.TrySetResult();

        public Task<Stream> ConnectAsync(string host, int port, CancellationToken ct) =>
            throw new NotSupportedException("no outbound connections in this test");

        public Task<Stream> ConnectTlsAsync(string host, int port, string serverName, bool validateCertificate, CancellationToken ct) =>
            throw new NotSupportedException("no outbound connections in this test");

        public Task<string?> GetStringAsync(Uri uri, IReadOnlyDictionary<string, string> pinnedHosts, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public ITgListener Listen(string host, int port)
        {
            var n = Interlocked.Increment(ref _listens);
            if (n == 1)
            {
                First = new RaceListener(onDisposed: () => FirstDisposed.TrySetResult());
                return First;
            }

            SecondListenEntered.TrySetResult();
            _releaseSecond.Task.GetAwaiter().GetResult();
            Second = new RaceListener(blockForever: true);
            return Second;
        }
    }

    private sealed class RaceListener : ITgListener
    {
        private readonly Action? _onDisposed;
        private readonly bool _blockForever;
        private int _accepts;
        private int _disposed;

        public RaceListener(Action? onDisposed = null, bool blockForever = false)
        {
            _onDisposed = onDisposed;
            _blockForever = blockForever;
        }

        public bool Disposed => Volatile.Read(ref _disposed) != 0;

        public async Task<(Stream Stream, string Label, IPAddress? RemoteAddress)> AcceptAsync(CancellationToken ct)
        {
            if (_blockForever)
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                throw new OperationCanceledException(ct);
            }

            if (Interlocked.Increment(ref _accepts) == 1)
            {
                throw new IOException("fake Accept failure");
            }

            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            throw new OperationCanceledException(ct);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _onDisposed?.Invoke();
            }
        }
    }
}
