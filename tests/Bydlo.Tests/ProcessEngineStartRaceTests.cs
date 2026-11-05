using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;

namespace Bydlo.Tests;

/// <summary>Issue 340: exit on the start boundary must not publish Running.</summary>
public sealed class ProcessEngineStartRaceTests
{
    private static EngineStartRequest Request() => new()
    {
        Settings = new AppSettings(),
        WorkDirectory = TestDirs.New("start-race"),
    };

    [Fact]
    public async Task Exit_between_final_check_and_running_reports_failure()
    {
        var runner = new BoundaryExitRunner(fireOnReadNumber: 2, exitCode: 1);
        var engine = new RaceEngine(runner);
        var states = new List<EngineState>();
        engine.StateChanged += (_, e) => states.Add(e.State);
        var ex = await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync(Request()));
        Assert.Contains("exited", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EngineState.Failed, engine.State);
        Assert.DoesNotContain(EngineState.Running, states);
        Assert.True(runner.Process!.HasExited);
        Assert.True(runner.Process.Stopped);
        Assert.True(runner.Process.Disposed);
    }

    [Fact]
    public async Task Exit_before_first_check_reports_failure()
    {
        var runner = new BoundaryExitRunner(fireOnReadNumber: 1, exitCode: 3);
        var engine = new RaceEngine(runner);
        await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync(Request()));
        Assert.Equal(EngineState.Failed, engine.State);
        Assert.True(runner.Process!.Stopped);
        Assert.True(runner.Process.Disposed);
    }

    [Fact]
    public async Task Exit_during_started_hook_reports_failure_without_running()
    {
        var runner = new BoundaryExitRunner(fireOnReadNumber: int.MaxValue, exitCode: 7);
        var engine = new RaceEngine(runner);
        var states = new List<EngineState>();
        engine.StateChanged += (_, e) => states.Add(e.State);
        engine.CrashAtStarted = () => runner.Process!.ForceExit(7);
        var ex = await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync(Request()));
        Assert.Contains("exited", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EngineState.Failed, engine.State);
        Assert.DoesNotContain(EngineState.Running, states);
        Assert.True(runner.Process!.Stopped);
        Assert.True(runner.Process.Disposed);
    }

    [Fact]
    public async Task Racing_exit_from_another_thread_never_leaves_running()
    {
        var runner = new BoundaryExitRunner(fireOnReadNumber: int.MaxValue, exitCode: 1);
        var engine = new RaceEngine(runner);
        var states = new List<EngineState>();
        engine.StateChanged += (_, e) => { lock (states) { states.Add(e.State); } };
        var starting = engine.StartAsync(Request());
        var spin = 0;
        while (runner.Process is null && spin++ < 100)
        {
            await Task.Delay(10);
        }
        Assert.NotNull(runner.Process);
        runner.Process!.ForceExit(1);
        try
        {
            await starting;
        }
        catch (EngineException)
        {
        }
        spin = 0;
        while (engine.State != EngineState.Failed && spin++ < 100)
        {
            await Task.Delay(10);
        }
        Assert.Equal(EngineState.Failed, engine.State);
        lock (states)
        {
            Assert.Equal(EngineState.Failed, states[^1]);
        }
    }

    [Fact]
    public async Task Clean_start_still_reaches_running()
    {
        var runner = new BoundaryExitRunner(fireOnReadNumber: int.MaxValue, exitCode: 1);
        var engine = new RaceEngine(runner);
        await engine.StartAsync(Request());
        Assert.Equal(EngineState.Running, engine.State);
        await engine.StopAsync();
        Assert.Equal(EngineState.Stopped, engine.State);
    }

    [Fact]
    public async Task Unexpected_exit_after_running_still_fails_with_cleanup()
    {
        var runner = new FakeProcessRunner();
        var engine = new RaceEngine(runner);
        await engine.StartAsync(Request());
        Assert.Equal(EngineState.Running, engine.State);
        runner.Last!.Crash(2);
        await Task.Delay(200);
        Assert.Equal(EngineState.Failed, engine.State);
    }

    [Fact]
    public async Task Concurrent_stop_and_exit_always_finish_stopped()
    {
        for (var i = 0; i < 100; i++)
        {
            var runner = new FakeProcessRunner();
            var engine = new RaceEngine(runner);
            await engine.StartAsync(Request());
            var process = runner.Last!;
            await Task.WhenAll(Task.Run(() => engine.StopAsync()), Task.Run(() => process.Crash(2)));
            Assert.Equal(EngineState.Stopped, engine.State);
            await engine.DisposeAsync();
        }
    }

    [Fact]
    public async Task Delayed_exit_from_previous_process_does_not_fail_restarted_engine()
    {
        var runner = new FakeProcessRunner();
        await using var engine = new RaceEngine(runner);
        await engine.StartAsync(Request());
        var previous = runner.Last!;
        await engine.StopAsync();
        await engine.StartAsync(Request());
        previous.Crash(2);
        Assert.Equal(EngineState.Running, engine.State);
    }

    private sealed class RaceEngine(IProcessRunner runner) : ProcessEngineBase(runner, new LogRedactor())
    {
        public Action? CrashAtStarted { get; set; }

        protected override TimeSpan ReadyTimeout => TimeSpan.Zero;
        public override EngineDescriptor Descriptor { get; } = new()
        {
            Kind = EngineKind.SingBox,
            DisplayName = "Fake race engine",
            License = "",
            Upstream = "",
            Protocols = new HashSet<ProtocolKind> { ProtocolKind.Trojan },
            Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Windows] = SupportLevel.Implemented },
        };
        public override bool CanHandle(Profile profile) => true;
        public override Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default) =>
            Task.FromResult(EngineAvailability.Ready("fake"));
        protected override Task<ProcessSpec> PrepareAsync(EngineStartRequest request, EngineAvailability availability, CancellationToken ct) =>
            Task.FromResult(new ProcessSpec { FileName = "fake", Arguments = [] });
        protected override Task OnStartedAsync(EngineStartRequest request, CancellationToken ct)
        {
            CrashAtStarted?.Invoke();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Returns false on the configured HasExited read while firing Exited synchronously,
    /// exactly like the issue repro. Later reads report the real exited state.
    /// </summary>
    private sealed class BoundaryExitRunner(int fireOnReadNumber, int exitCode) : IProcessRunner
    {
        public BoundaryExitProcess? Process { get; private set; }

        public IManagedProcess Start(ProcessSpec spec)
        {
            Process = new BoundaryExitProcess(fireOnReadNumber, exitCode);
            return Process;
        }

        public Task<(int ExitCode, string Output)> RunToCompletionAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct = default) =>
            Task.FromResult((0, "ok"));
    }

    private sealed class BoundaryExitProcess(int fireOnReadNumber, int exitCode) : IManagedProcess
    {
        private int _reads;
        private bool _exited;

        public int Id => 4242;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
        public int? ExitCode { get; private set; }
        public bool Stopped { get; private set; }
        public bool Disposed { get; private set; }

        public bool HasExited
        {
            get
            {
                _reads++;
                if (_reads == fireOnReadNumber && !_exited)
                {
                    ForceExit(exitCode);
                    return false;
                }
                return _exited;
            }
        }

        public void ForceExit(int code)
        {
            _exited = true;
            ExitCode = code;
            Exited?.Invoke(this, code);
        }

#pragma warning disable CS0067
        public event EventHandler<string>? OutputLine;
#pragma warning restore CS0067
        public event EventHandler<int>? Exited;

        public Task StopAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            Stopped = true;
            _exited = true;
            ExitCode ??= 0;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
