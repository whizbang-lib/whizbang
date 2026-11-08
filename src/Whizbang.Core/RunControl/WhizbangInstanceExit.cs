// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.RunControl;

/// <summary>
/// The one exit path (#2126): see <see cref="IWhizbangInstanceExit"/>. This file is the one place in library code that
/// stops the host and sets the process exit code; the banned-symbol analyzer (<c>BannedSymbols.Exit.txt</c>) and the
/// source guard fail the build anywhere else.
/// </summary>
/// <remarks>
/// <para>
/// Everything the exit reports is read when it begins, before the halt changes it: the stage and phase it was in, what
/// it held. The halt then releases the work and roles, the facts are recorded (with the eviction tombstone read in the
/// same call), the one entry is logged, the exit code is set, and the host is stopped. Nothing it does throws: an exit is
/// what a failure leads to, so a step that fails is said in the entry and the next step still runs.
/// </para>
/// <para>
/// Collaborators are resolved when the exit happens, not when the path is built, so every service the exit reads about
/// (the heartbeat, the role elector, the claim worker) can itself take the path in its constructor.
/// </para>
/// </remarks>
/// <docs>operations/startup/instance-exits</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/RunControl/WhizbangInstanceExitTests.cs</tests>
public sealed class WhizbangInstanceExit : IWhizbangInstanceExit {
  /// <summary>The page that explains every exit, linked from each exit's log entry.</summary>
  public const string DOCS_LINK = "https://whizbang-lib.github.io/docs/v1.0.0/operations/startup/instance-exits";

  /// <summary>The event id of the one entry an exit writes.</summary>
  public static readonly EventId ExitEventId = new(1, "InstanceExit");

  private readonly IServiceProvider _services;
  private readonly IWhizbangRunController? _runControl;
  private readonly IProcessExitCode _exitCode;
  private readonly TimeProvider _time;
  private readonly ILogger _logger;
  private readonly Lock _gate = new();
  private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
  private InstanceExitRequest? _request;
  private bool _completing;
  private bool _hosted;
  private DateTimeOffset? _hostStartedAt;
  private InstanceExitReport? _report;

  /// <summary>Creates the path.</summary>
  /// <param name="services">The host's services, read when the exit happens.</param>
  /// <param name="runControl">
  /// The run-control path the exit halts through; null in a host without one (no worker pipeline), whose exit has nothing
  /// to halt or release.
  /// </param>
  /// <param name="exitCode">Sets the process exit code.</param>
  /// <param name="timeProvider">The clock uptime and heartbeat age are measured on; the system clock when null.</param>
  /// <param name="logger">Where the exit's entry is written.</param>
  public WhizbangInstanceExit(
      IServiceProvider services,
      IWhizbangRunController? runControl,
      IProcessExitCode exitCode,
      TimeProvider? timeProvider,
      ILogger<WhizbangInstanceExit> logger) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(exitCode);
    ArgumentNullException.ThrowIfNull(logger);
    _services = services;
    _runControl = runControl;
    _exitCode = exitCode;
    _time = timeProvider ?? TimeProvider.System;
    _logger = logger;
  }

  /// <inheritdoc />
  public ExitCause? Cause {
    get {
      lock (_gate) {
        return _request?.Cause;
      }
    }
  }

  /// <inheritdoc />
  public InstanceExitReport? Report => Volatile.Read(ref _report);

  /// <inheritdoc />
  public Task Completion => _completion.Task;

  /// <inheritdoc />
  public void RequestExit(InstanceExitRequest request) {
    ArgumentNullException.ThrowIfNull(request);
    bool hosted;
    lock (_gate) {
      if (_request is not null) {
        return;   // the first cause wins: the process exits once
      }
      _request = request;
      hosted = _hosted;
    }
    if (hosted && _services.GetService<IHostApplicationLifetime>() is { } lifetime) {
      // The host's stop completes the exit, on the host's stop token (OnHostStoppingAsync).
#pragma warning disable RS0030 // the one place library code stops the host (#2126)
      lifetime.StopApplication();
#pragma warning restore RS0030
      return;
    }
    // No host to stop first: the exit completes itself. It never throws, so nothing is lost by not awaiting it here.
    _ = _completeOnceAsync(CancellationToken.None);
  }

  /// <inheritdoc />
  public async Task ExitAsync(InstanceExitRequest request, CancellationToken cancellationToken) {
    RequestExit(request);
    await _completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public void OnHostStarting() {
    lock (_gate) {
      _hosted = true;
      _hostStartedAt = _time.GetUtcNow();
    }
  }

  /// <inheritdoc />
  public async Task OnHostStoppingAsync(CancellationToken stopToken) {
    var unrequested = _unrequestedStop();
    lock (_gate) {
      _request ??= unrequested;
    }
    try {
      await _completeOnceAsync(stopToken).ConfigureAwait(false);
    } catch (OperationCanceledException) when (stopToken.IsCancellationRequested) {
      // The host's stop budget ran out while another caller's exit was still completing; the host stops anyway.
    }
  }

  /// <summary>
  /// A host stop no cause asked for: a crash when a hosted worker faulted (the host stops itself on a faulted
  /// <see cref="BackgroundService"/>), otherwise a host shutdown.
  /// </summary>
  private InstanceExitRequest _unrequestedStop() {
    foreach (var hosted in _services.GetServices<IHostedService>()) {
      if (hosted is BackgroundService { ExecuteTask: { IsFaulted: true } faulted }) {
        var failure = faulted.Exception.GetBaseException();
        return new InstanceExitRequest(ExitCause.Crash,
          $"the hosted service {TypeNameFormatter.DisplayName(hosted.GetType())} failed, and the host stops on a failed hosted service",
          failure);
      }
    }
    return new InstanceExitRequest(ExitCause.HostShutdown, "the host was asked to stop");
  }

  private Task _completeOnceAsync(CancellationToken cancellationToken) {
    lock (_gate) {
      if (_completing) {
        return _completion.Task.WaitAsync(cancellationToken);
      }
      _completing = true;
    }
    return _completeAsync(cancellationToken);
  }

  private async Task _completeAsync(CancellationToken cancellationToken) {
    InstanceExitRequest request;
    lock (_gate) {
      request = _request!;
    }
    // Read before the halt changes them: where the instance was and what it held.
    var record = _factsOf(request);
    var haltFailure = await _haltAsync(cancellationToken).ConfigureAwait(false);
    var recording = await _recordAsync(record, cancellationToken).ConfigureAwait(false);
    var entry = new InstanceExitLogEntry(record, recording, haltFailure, DOCS_LINK);
    _logger.Log(record.Intended ? LogLevel.Information : LogLevel.Error, ExitEventId, entry, request.Exception,
      static (state, _) => state.ToString());
    _exitCode.SetExitCode(record.ExitCode);
    Volatile.Write(ref _report, new InstanceExitReport(record, recording));
    if (_services.GetService<IHostApplicationLifetime>() is { ApplicationStopping.IsCancellationRequested: false } lifetime) {
#pragma warning disable RS0030 // the one place library code stops the host (#2126)
      lifetime.StopApplication();
#pragma warning restore RS0030
    }
    _completion.TrySetResult();
  }

  private InstanceExitRecord _factsOf(InstanceExitRequest request) {
    var instance = _services.GetService<IServiceInstanceProvider>();
    var now = _time.GetUtcNow();
    DateTimeOffset? startedAt;
    lock (_gate) {
      startedAt = _hostStartedAt;
    }
    var lastBeat = _services.GetService<ILastAcceptedHeartbeat>()?.LastAcceptedAt;
    var exitCode = ExitCauses.ExitCodeOf(request.Cause);
    return new InstanceExitRecord(
      instance?.InstanceId ?? Guid.Empty,
      instance?.ServiceName ?? "unknown",
      instance?.HostName ?? Environment.MachineName,
      instance?.ProcessId ?? Environment.ProcessId,
      request.Cause, exitCode, exitCode == 0, request.Reason,
      request.Exception is { } ex ? TypeNameFormatter.DisplayName(ex.GetType()) : null,
      request.Exception?.Message,
      _services.GetService<IStartupStageReporter>()?.Current?.Name,
      _services.GetService<IWhizbangLifecycleState>()?.Phase.ToString(),
      startedAt is { } started ? (now - started).TotalSeconds : null,
      lastBeat is { } beat ? (now - beat).TotalSeconds : null,
      [.. _services.GetServices<IReleasesHeldRoles>().SelectMany(r => r.HeldRoles).Order(StringComparer.Ordinal)],
      _services.GetServices<IReleasesHeldWork>().Sum(w => w.HeldLeases),
      _runControl?.Permit.Held ?? 0,
      _services.GetService<ILibraryVersionProvider>()?.LibraryVersion);
  }

  /// <summary>The run-control halt with the stop reason; what went wrong, or null when it completed.</summary>
  private async Task<string?> _haltAsync(CancellationToken cancellationToken) {
    if (_runControl is not { } runControl) {
      return "this host has no run-control path, so there was nothing to halt";
    }
    try {
      await runControl.HaltAsync(RunHaltReason.Stop, cancellationToken).ConfigureAwait(false);
      return null;
    }
#pragma warning disable CA1031 // not swallowed: the failure is stated in the exit's one entry, and the exit goes on
    catch (Exception ex) {
#pragma warning restore CA1031
      return TypeNameFormatter.DisplayName(ex.GetType()) + ": " + ex.Message;
    }
  }

  /// <summary>Records the facts in <c>wh_instance_exits</c>, best effort: the answer says why when they were not.</summary>
  private async Task<InstanceExitRecordResult> _recordAsync(InstanceExitRecord record, CancellationToken cancellationToken) {
    if (record.InstanceId == Guid.Empty) {
      return InstanceExitRecordResult.NotWritten("this host has no instance identity to record the exit against");
    }
    try {
      await using var scope = _services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
      if (scope.ServiceProvider.GetService<IWorkCoordinator>() is not { } coordinator) {
        return InstanceExitRecordResult.NotWritten("this host has no work coordinator, so it keeps no instance records");
      }
      return await coordinator.RecordInstanceExitAsync(record, cancellationToken).ConfigureAwait(false);
    }
#pragma warning disable CA1031 // not swallowed: the entry says the record could not be written, and why
    catch (Exception ex) {
#pragma warning restore CA1031
      return InstanceExitRecordResult.NotWritten(
        "the database could not be reached or refused the write (" + TypeNameFormatter.DisplayName(ex.GetType()) + ": " + ex.Message + ")");
    }
  }
}

/// <summary>The default <see cref="IProcessExitCode"/>: sets <c>Environment.ExitCode</c>, the code a host process exits with.</summary>
/// <docs>operations/startup/instance-exits#exit-codes</docs>
/// <tests>tests/Whizbang.Core.Tests/RunControl/ExitCauseContractTests.cs</tests>
public sealed class ProcessExitCode : IProcessExitCode {
  /// <summary>The one instance.</summary>
  public static ProcessExitCode Instance { get; } = new();

  private ProcessExitCode() { }

  /// <inheritdoc />
  public void SetExitCode(int exitCode) {
#pragma warning disable RS0030 // the one place library code sets the exit code (#2126)
    Environment.ExitCode = exitCode;
#pragma warning restore RS0030
  }
}
