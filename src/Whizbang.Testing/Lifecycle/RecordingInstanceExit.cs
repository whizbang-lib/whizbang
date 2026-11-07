// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.RunControl;

namespace Whizbang.Testing.Lifecycle;

/// <summary>
/// An <see cref="IWhizbangInstanceExit"/> for tests (#2126): records every exit requested of it and completes the first at
/// once, without halting, logging, recording or stopping anything. A test that hand-builds a worker gives it this and
/// asserts on <see cref="Requests"/>, or waits for <see cref="FirstRequest"/>: the exit's own signal, never a delay.
/// </summary>
/// <docs>operations/startup/instance-exits</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupStageHostLifecycleTests.cs</tests>
public sealed class RecordingInstanceExit : IWhizbangInstanceExit {
  private readonly Lock _gate = new();
  private readonly List<InstanceExitRequest> _requests = [];
  private readonly TaskCompletionSource<InstanceExitRequest> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

  /// <summary>Every request, in order, including the ones the first made redundant.</summary>
  public IReadOnlyList<InstanceExitRequest> Requests {
    get {
      lock (_gate) {
        return [.. _requests];
      }
    }
  }

  /// <summary>Completes with the first request.</summary>
  public Task<InstanceExitRequest> FirstRequest => _first.Task;

  /// <summary>How many times the host's start was reported.</summary>
  public int HostStartingCalls { get; private set; }

  /// <summary>How many times the host's stop was reported.</summary>
  public int HostStoppingCalls { get; private set; }

  /// <inheritdoc />
  public ExitCause? Cause {
    get {
      lock (_gate) {
        return _requests.Count == 0 ? null : _requests[0].Cause;
      }
    }
  }

  /// <inheritdoc />
  public InstanceExitReport? Report => null;

  /// <inheritdoc />
  public Task Completion => _first.Task;

  /// <inheritdoc />
  public void RequestExit(InstanceExitRequest request) {
    ArgumentNullException.ThrowIfNull(request);
    // Not implemented yet.
  }

  /// <inheritdoc />
  public Task ExitAsync(InstanceExitRequest request, CancellationToken cancellationToken) {
    RequestExit(request);
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public void OnHostStarting() => HostStartingCalls++;

  /// <inheritdoc />
  public Task OnHostStoppingAsync(CancellationToken stopToken) {
    HostStoppingCalls++;
    return Task.CompletedTask;
  }
}
