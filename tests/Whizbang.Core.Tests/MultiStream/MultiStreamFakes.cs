// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.MultiStream;

namespace Whizbang.Core.Tests.MultiStream;

/// <summary>A multi-stream command carrying a value of its own, so a test can see the payload survive the store.</summary>
public sealed record ProbeMultiStreamCommand(Guid RunId, IReadOnlyList<Guid> StreamIds, string Label) : IMultiStreamCommand;

/// <summary>A second command type, for tests that need two bindings.</summary>
public sealed record OtherProbeMultiStreamCommand(Guid RunId, IReadOnlyList<Guid> StreamIds) : IMultiStreamCommand;

/// <summary>Records each stream it is called for, with the command it was handed.</summary>
public sealed class ProbeMultiStreamHandler : IMultiStreamCommandHandler<ProbeMultiStreamCommand> {
  private readonly List<(ProbeMultiStreamCommand Command, Guid StreamId)> _calls = [];

  public IReadOnlyList<(ProbeMultiStreamCommand Command, Guid StreamId)> Calls {
    get {
      lock (_calls) {
        return [.. _calls];
      }
    }
  }

  public ValueTask HandleStreamAsync(ProbeMultiStreamCommand command, Guid streamId, CancellationToken cancellationToken) {
    lock (_calls) {
      _calls.Add((command, streamId));
    }
    return ValueTask.CompletedTask;
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
      Guid instanceId, IReadOnlyList<MultiStreamOutcome> outcomes, int maxAttempts, CancellationToken cancellationToken) =>
    throw new NotSupportedException("The intake never completes.");

  public Task<MultiStreamRunStatus?> ReadRunAsync(Guid runId, CancellationToken cancellationToken) =>
    throw new NotSupportedException("The intake never reads a run.");

  public Task<int> PurgeCompletedAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken) =>
    throw new NotSupportedException("The intake never purges.");
}
