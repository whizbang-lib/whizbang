// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.MultiStream;

namespace Whizbang.Core.Tests.MultiStream;

/// <summary>A multi-stream command carrying a value of its own, so a test can see the payload survive the store.</summary>
public sealed record ProbeMultiStreamCommand(Guid RunId, IReadOnlyList<Guid> StreamIds, string Label) : IMultiStreamCommand;

/// <summary>A second command type, for tests that need two bindings.</summary>
public sealed record OtherProbeMultiStreamCommand(Guid RunId, IReadOnlyList<Guid> StreamIds) : IMultiStreamCommand;

/// <summary>Records each stream it is called for, with the command it was handed. Subclassed for other behaviors.</summary>
public class ProbeMultiStreamHandler : IMultiStreamCommandHandler<ProbeMultiStreamCommand> {
  private readonly List<(ProbeMultiStreamCommand Command, Guid StreamId)> _calls = [];

  public IReadOnlyList<(ProbeMultiStreamCommand Command, Guid StreamId)> Calls {
    get {
      lock (_calls) {
        return [.. _calls];
      }
    }
  }

  public virtual ValueTask HandleStreamAsync(ProbeMultiStreamCommand command, Guid streamId, CancellationToken cancellationToken) {
    cancellationToken.ThrowIfCancellationRequested();
    lock (_calls) {
      _calls.Add((command, streamId));
    }
    return ValueTask.CompletedTask;
  }
}

/// <summary>Throws for one stream, records the rest.</summary>
public sealed class ThrowingOnStreamHandler(Guid failing) : ProbeMultiStreamHandler {
  public override ValueTask HandleStreamAsync(ProbeMultiStreamCommand command, Guid streamId, CancellationToken cancellationToken) =>
    streamId == failing
      ? throw new InvalidOperationException("refused " + streamId)
      : base.HandleStreamAsync(command, streamId, cancellationToken);
}

/// <summary>Works one stream, then cancels the token, as a host stopping mid-batch would.</summary>
public sealed class CancelingAfterStreamHandler(Guid last, CancellationTokenSource cancel) : ProbeMultiStreamHandler {
  public override async ValueTask HandleStreamAsync(ProbeMultiStreamCommand command, Guid streamId, CancellationToken cancellationToken) {
    await base.HandleStreamAsync(command, streamId, cancellationToken);
    if (streamId == last) {
      await cancel.CancelAsync();
    }
  }
}

/// <summary>Cancels on its own, as a handler whose own timeout fired would, while the host keeps running.</summary>
public sealed class TimingOutHandler : ProbeMultiStreamHandler {
  public override ValueTask HandleStreamAsync(ProbeMultiStreamCommand command, Guid streamId, CancellationToken cancellationToken) =>
    throw new TaskCanceledException("timed out");
}

/// <summary>Records the ambient scope each call ran under.</summary>
public sealed class ScopeCapturingHandler : ProbeMultiStreamHandler {
  public List<Whizbang.Core.Security.IScopeContext?> Seen { get; } = [];

  public override ValueTask HandleStreamAsync(ProbeMultiStreamCommand command, Guid streamId, CancellationToken cancellationToken) {
    Seen.Add(Whizbang.Core.Security.ScopeContextAccessor.CurrentContext);
    return base.HandleStreamAsync(command, streamId, cancellationToken);
  }
}

/// <summary>
/// A store whose claims and completions are scripted per call, and which records everything asked of it.
/// </summary>
public sealed class ScriptedMultiStreamStore : IMultiStreamCommandStore {
  public Queue<IReadOnlyList<MultiStreamWorkItem>> Claims { get; } = new();
  public Queue<IReadOnlyList<MultiStreamRunCompletion>> CompletionsToReturn { get; } = new();
  public List<MultiStreamClaim> ClaimRequests { get; } = [];
  public List<(Guid InstanceId, IReadOnlyList<MultiStreamOutcome> Outcomes, int MaxAttempts, TimeSpan ReleaseDelay)> Completed { get; } = [];
  public List<DateTimeOffset> Purges { get; } = [];

  /// <summary>When set, the next claim throws it (once).</summary>
  public Exception? ClaimFailure { get; set; }

  /// <summary>Raised after each claim, for tests that wait on the worker reaching the store.</summary>
  public event Action<MultiStreamClaim>? OnClaimed;

  public Task<bool> RecordRunAsync(MultiStreamRun run, CancellationToken cancellationToken) =>
    throw new NotSupportedException("The worker never records runs.");

  public Task<IReadOnlyList<MultiStreamWorkItem>> ClaimAsync(MultiStreamClaim claim, CancellationToken cancellationToken) {
    lock (ClaimRequests) {
      ClaimRequests.Add(claim);
    }
    try {
      if (ClaimFailure is { } failure) {
        ClaimFailure = null;
        throw failure;
      }
      IReadOnlyList<MultiStreamWorkItem> items = Claims.TryDequeue(out var next) ? next : [];
      return Task.FromResult(items);
    } finally {
      OnClaimed?.Invoke(claim);
    }
  }

  public Task<IReadOnlyList<MultiStreamRunCompletion>> CompleteAsync(
      Guid instanceId, IReadOnlyList<MultiStreamOutcome> outcomes, int maxAttempts, TimeSpan releaseDelay, CancellationToken cancellationToken) {
    Completed.Add((instanceId, outcomes, maxAttempts, releaseDelay));
    IReadOnlyList<MultiStreamRunCompletion> completions = CompletionsToReturn.TryDequeue(out var next) ? next : [];
    return Task.FromResult(completions);
  }

  public Task<MultiStreamRunStatus?> ReadRunAsync(Guid runId, CancellationToken cancellationToken) =>
    throw new NotSupportedException("The worker never reads a run.");

  public Task<int> PurgeCompletedAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken) {
    Purges.Add(completedBefore);
    return Task.FromResult(0);
  }
}

/// <summary>A different handler for the same command, to prove one command has one handler.</summary>
public sealed class SecondProbeMultiStreamHandler : IMultiStreamCommandHandler<ProbeMultiStreamCommand> {
  public ValueTask HandleStreamAsync(ProbeMultiStreamCommand command, Guid streamId, CancellationToken cancellationToken) =>
    ValueTask.CompletedTask;
}

/// <summary>Handles the second command type.</summary>
public sealed class OtherProbeMultiStreamHandler : IMultiStreamCommandHandler<OtherProbeMultiStreamCommand> {
  public ValueTask HandleStreamAsync(OtherProbeMultiStreamCommand command, Guid streamId, CancellationToken cancellationToken) =>
    ValueTask.CompletedTask;
}

/// <summary>Keeps every run it is asked to record. <see cref="AlreadyRecorded"/> answers as a store that had it.</summary>
public sealed class RecordingMultiStreamStore : IMultiStreamCommandStore {
  public List<MultiStreamRun> Recorded { get; } = [];

  public bool AlreadyRecorded { get; init; }

  public Task<bool> RecordRunAsync(MultiStreamRun run, CancellationToken cancellationToken) {
    Recorded.Add(run);
    return Task.FromResult(!AlreadyRecorded);
  }

  public Task<IReadOnlyList<MultiStreamWorkItem>> ClaimAsync(MultiStreamClaim claim, CancellationToken cancellationToken) =>
    throw new NotSupportedException("The intake never claims.");

  public Task<IReadOnlyList<MultiStreamRunCompletion>> CompleteAsync(
      Guid instanceId, IReadOnlyList<MultiStreamOutcome> outcomes, int maxAttempts, TimeSpan releaseDelay, CancellationToken cancellationToken) =>
    throw new NotSupportedException("The intake never completes.");

  public Task<MultiStreamRunStatus?> ReadRunAsync(Guid runId, CancellationToken cancellationToken) =>
    throw new NotSupportedException("The intake never reads a run.");

  public Task<int> PurgeCompletedAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken) =>
    throw new NotSupportedException("The intake never purges.");
}
