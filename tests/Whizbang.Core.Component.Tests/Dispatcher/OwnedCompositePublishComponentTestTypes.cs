// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;

// Types for OwnedCompositePublishComponentTests. An owned composite of six inner events on one stream, each inner
// event with a local receptor, discovered by the source generator so the tests run the REAL generated dispatcher.

namespace Whizbang.Core.Component.Tests.CompositePublish.Owned;

public static class CompositeProbe {
  private static readonly ConcurrentDictionary<(Guid, string), int> _handled = new();

  public static void RecordHandled(Guid streamId, string inner) => _handled.AddOrUpdate((streamId, inner), 1, static (_, n) => n + 1);

  public static int HandledCount(Guid streamId, string inner) => _handled.TryGetValue((streamId, inner), out var n) ? n : 0;
}

public sealed class CpWorkflowComposite : CompositeEventBase;

public record CpAssigned([property: StreamId] Guid StreamId) : IEvent;
public record CpStepAssigned([property: StreamId] Guid StreamId) : IEvent;
public record CpStepApproved([property: StreamId] Guid StreamId) : IEvent;
public record CpStageCompleted([property: StreamId] Guid StreamId) : IEvent;
public record CpStageStarted([property: StreamId] Guid StreamId) : IEvent;
public record CpInitialized([property: StreamId] Guid StreamId) : IEvent;

public sealed class CpAssignedReceptor : IReceptor<CpAssigned> {
  public ValueTask HandleAsync(CpAssigned message, CancellationToken cancellationToken = default) {
    CompositeProbe.RecordHandled(message.StreamId, nameof(CpAssigned));
    return ValueTask.CompletedTask;
  }
}

public sealed class CpStepAssignedReceptor : IReceptor<CpStepAssigned> {
  public ValueTask HandleAsync(CpStepAssigned message, CancellationToken cancellationToken = default) {
    CompositeProbe.RecordHandled(message.StreamId, nameof(CpStepAssigned));
    return ValueTask.CompletedTask;
  }
}

public sealed class CpStepApprovedReceptor : IReceptor<CpStepApproved> {
  public ValueTask HandleAsync(CpStepApproved message, CancellationToken cancellationToken = default) {
    CompositeProbe.RecordHandled(message.StreamId, nameof(CpStepApproved));
    return ValueTask.CompletedTask;
  }
}

public sealed class CpStageCompletedReceptor : IReceptor<CpStageCompleted> {
  public ValueTask HandleAsync(CpStageCompleted message, CancellationToken cancellationToken = default) {
    CompositeProbe.RecordHandled(message.StreamId, nameof(CpStageCompleted));
    return ValueTask.CompletedTask;
  }
}

public sealed class CpStageStartedReceptor : IReceptor<CpStageStarted> {
  public ValueTask HandleAsync(CpStageStarted message, CancellationToken cancellationToken = default) {
    CompositeProbe.RecordHandled(message.StreamId, nameof(CpStageStarted));
    return ValueTask.CompletedTask;
  }
}

public sealed class CpInitializedReceptor : IReceptor<CpInitialized> {
  public ValueTask HandleAsync(CpInitialized message, CancellationToken cancellationToken = default) {
    CompositeProbe.RecordHandled(message.StreamId, nameof(CpInitialized));
    return ValueTask.CompletedTask;
  }
}

/// <summary>A message that is neither a command nor an event, such as a notice or a reply.</summary>
public record CpNotice(Guid Id) : IMessage;

/// <summary>A command whose receptor returns an owned composite, so the composite reaches the outbox through a cascade.</summary>
public record CpStartWorkflow(Guid StreamId) : ICommand;

public sealed class CpStartWorkflowReceptor : IReceptor<CpStartWorkflow, CpWorkflowComposite> {
  public ValueTask<CpWorkflowComposite> HandleAsync(CpStartWorkflow message, CancellationToken cancellationToken = default) =>
    ValueTask.FromResult(new CpWorkflowComposite { StreamId = message.StreamId, Inner = [new CpStepApproved(message.StreamId)] });
}
