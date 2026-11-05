using Bydlo.Core.Models;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;

namespace Bydlo.Core.Engines;

/// <summary>
/// Base class for engines that wrap one external process: start, watch output, detect readiness,
/// report unexpected exits, stop the process tree.
/// </summary>
public abstract class ProcessEngineBase(IProcessRunner runner, LogRedactor redactor) : IEngine
{
    private IManagedProcess? _process;
    private TaskCompletionSource? _ready;
    private volatile bool _stopping;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private int? _exitDuringStart;
    private string? _exitDuringStartFatal;

    /// <summary>
    /// Serializes the final exit check with the Running transition (issue #340):
    /// an exit landing between the check and SetState must either be observed
    /// by the check or drive Failed cleanup afterwards, never a stuck Running.
    /// A plain monitor (reentrant) is used on purpose: a synchronous exit event
    /// fired from inside the HasExited read must record instead of deadlocking.
    /// </summary>
    private readonly object _startGate = new();

    public abstract EngineDescriptor Descriptor { get; }

    public EngineState State { get; private set; } = EngineState.Stopped;

    public event EventHandler<EngineStateChangedEventArgs>? StateChanged;

    public event EventHandler<string>? Log;

    public event EventHandler<TrafficSample>? Traffic;

    protected IProcessRunner Runner => runner;

    protected LogRedactor Redactor => redactor;

    /// <summary>How long to wait for <see cref="IsReadyLine"/> before assuming the process is up.</summary>
    protected virtual TimeSpan ReadyTimeout => TimeSpan.FromSeconds(8);

    /// <summary>When true, absence of a ready line within <see cref="ReadyTimeout"/> while still alive counts as started.</summary>
    protected virtual bool AliveMeansReady => true;

    public abstract bool CanHandle(Profile profile);

    public abstract Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default);

    /// <summary>Builds the process to launch (write configs into <see cref="EngineStartRequest.WorkDirectory"/>).</summary>
    protected abstract Task<ProcessSpec> PrepareAsync(EngineStartRequest request, EngineAvailability availability, CancellationToken ct);

    /// <summary>Line that proves the engine is serving traffic.</summary>
    protected virtual bool IsReadyLine(string line) => false;

    /// <summary>Line that proves start-up failed; returns a user-friendly message.</summary>
    protected virtual string? TryParseFatal(string line) =>
        line.Contains("FATAL", StringComparison.OrdinalIgnoreCase) ? line : null;

    protected virtual Task OnStartedAsync(EngineStartRequest request, CancellationToken ct) => Task.CompletedTask;

    protected virtual Task OnStoppingAsync() => Task.CompletedTask;

    protected void EmitTraffic(TrafficSample sample) => Traffic?.Invoke(this, sample);

    protected void EmitLog(string line) => Log?.Invoke(this, redactor.Redact(line));

    public async Task StartAsync(EngineStartRequest request, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try { await StartCoreAsync(request, ct).ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task StartCoreAsync(EngineStartRequest request, CancellationToken ct)
    {
        if (_process is not null || State is EngineState.Running or EngineState.Starting)
        {
            throw new EngineException($"{Descriptor.DisplayName} is already running.");
        }

        SetState(EngineState.Starting);
        try
        {
            var availability = await CheckAvailabilityAsync(request.Settings, ct).ConfigureAwait(false);
            if (!availability.IsReady)
            {
                throw new EngineException(availability.Message, localizationKey: availability.LocalizationKey);
            }

            foreach (var secret in request.Secrets.Values.Values)
            {
                redactor.RegisterSecret(secret);
            }

            // Chain hops carry their own secrets; every one must be masked before configs are built.
            if (request.GroupMembers is { } members)
            {
                foreach (var member in members)
                {
                    foreach (var secret in member.Secrets.Values.Values) { redactor.RegisterSecret(secret); }
                }
            }
            if (request.Chain is { } chainHops)
            {
                foreach (var hop in chainHops)
                {
                    foreach (var secret in hop.Secrets.Values.Values)
                    {
                        redactor.RegisterSecret(secret);
                    }
                }
            }

            Directory.CreateDirectory(request.WorkDirectory);
            var spec = await PrepareAsync(request, availability, ct).ConfigureAwait(false);
            _stopping = false;
            _exitDuringStart = null;
            _exitDuringStartFatal = null;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ready = ready;
            string? fatal = null;

            var process = runner.Start(spec);
            _process = process;
            process.OutputLine += (_, line) =>
            {
                EmitLog(line);
                if (IsReadyLine(line))
                {
                    ready.TrySetResult();
                }
                else if (TryParseFatal(line) is { } f)
                {
                    Interlocked.CompareExchange(ref fatal, redactor.Redact(f), null);
                }
            };
            process.Exited += (_, code) => OnProcessExited(process, code, Volatile.Read(ref fatal));

            var timeout = Task.Delay(ReadyTimeout, ct);
            var finished = await Task.WhenAny(_ready.Task, timeout).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var exitedEarly = _process.HasExited;
            if (exitedEarly || _exitDuringStart.HasValue)
            {
                throw new EngineException(_exitDuringStartFatal ?? Volatile.Read(ref fatal) ?? $"{Descriptor.DisplayName} exited with code {_exitDuringStart ?? _process.ExitCode}.");
            }

            if (finished == timeout && !AliveMeansReady)
            {
                throw new EngineException($"{Descriptor.DisplayName} did not report readiness in {ReadyTimeout.TotalSeconds:0}s.");
            }

            await OnStartedAsync(request, ct).ConfigureAwait(false);
            lock (_startGate)
            {
                var exitedLate = _process.HasExited;
                if (exitedLate || _exitDuringStart.HasValue)
                {
                    throw new EngineException(_exitDuringStartFatal ?? Volatile.Read(ref fatal) ?? $"{Descriptor.DisplayName} exited during startup.");
                }

                SetState(EngineState.Running);
            }
        }
        catch (Exception ex)
        {
            try { await StopCoreAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanupError)
            {
                EmitLog("Cleanup failed; recovery is required: " + cleanupError.Message);
                ex = new AggregateException(ex, cleanupError);
            }
            SetState(EngineState.Failed, ex.Message);
            throw ex as EngineException ?? new EngineException(ex.Message, ex);
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try { await StopSerializedAsync(ct).ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopSerializedAsync(CancellationToken ct)
    {
        _stopping = true;
        SetState(EngineState.Stopping);
        try
        {
            await StopCoreAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            SetState(EngineState.Stopped);
        }
        catch (Exception ex)
        {
            SetState(EngineState.Failed, ex.Message);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void OnProcessExited(IManagedProcess process, int code, string? fatal)
    {
        lock (_startGate)
        {
            if (!ReferenceEquals(process, _process) || _stopping || State is EngineState.Stopped) { return; }
            _ready?.TrySetResult();
            // The state read must hold the gate: otherwise an exit racing the
            // final check in StartAsync would be recorded as "during start"
            // after Running was already published and then dropped.
            if (State is EngineState.Starting)
            {
                _exitDuringStart = code;
                _exitDuringStartFatal = fatal;
                return;
            }
        }

        _ = CleanupAfterExitAsync(code, fatal, process);
    }

    private async Task CleanupAfterExitAsync(int code, string? fatal, IManagedProcess? process)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopping || State != EngineState.Running || !ReferenceEquals(process, _process)) { return; }
            var message = fatal ?? $"{Descriptor.DisplayName} exited unexpectedly (code {code}).";
            try { await OnStoppingAsync().ConfigureAwait(false); }
            catch (Exception ex) { message += " Cleanup failed: " + ex.Message; }
            SetState(EngineState.Failed, message);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        _stopping = true;
        var errors = new List<Exception>();
        // Also needed when Prepare/Start failed before a process was assigned.
        try { await OnStoppingAsync().ConfigureAwait(false); }
        catch (Exception ex) { errors.Add(ex); }
        if (_process is not null)
        {
            try
            {
                await _process.StopAsync(timeout, ct).ConfigureAwait(false);
                await _process.DisposeAsync().ConfigureAwait(false);
                _process = null;
            }
            catch (Exception ex) { errors.Add(ex); } // Keep the handle so stop/dispose can be retried.
        }

        if (errors.Count > 0) { throw new AggregateException("Engine cleanup failed.", errors); }
    }

    protected void SetState(EngineState state, string? error = null)
    {
        lock (_startGate)
        {
            State = state;
            StateChanged?.Invoke(this, new EngineStateChangedEventArgs(state, error));
        }
    }
}

