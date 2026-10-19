// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Messaging;

// Types for EveryReceptorInPriorityOrderComponentTests (#1412). A command and an event each have three
// local receptors declared out of priority order, so the order they run in shows the priority was read;
// the receptor with no [ReceptorPriority] sits between an early and a late one, which shows the default
// is in the middle of the range. The source generator discovers every receptor here.

namespace Whizbang.Core.Component.Tests.ReceptorPriorityOrder;

/// <summary>The receptors that ran, in order, per message id. Each test uses a fresh id.</summary>
public static class PriorityProbe {
  private static readonly ConcurrentDictionary<Guid, ConcurrentQueue<string>> _ran = new();

  public static void Record(Guid id, string receptor) => _ran.GetOrAdd(id, static _ => new()).Enqueue(receptor);

  public static string[] Ran(Guid id) => _ran.TryGetValue(id, out var q) ? [.. q] : [];
}

public record PrCommand(Guid Id) : ICommand;

public record PrResult(Guid Id, string AnsweredBy);

/// <summary>A command two receptors answer with a result: the earlier one by priority answers the caller.</summary>
public record PrTwoAnswersCommand(Guid Id) : ICommand;

public record PrEvent([property: StreamId] Guid Id) : IEvent;

[ReceptorPriority(ReceptorPriority.LATE)]
public sealed class PrCommandLate : IReceptor<PrCommand> {
  public ValueTask HandleAsync(PrCommand message, CancellationToken cancellationToken = default) {
    PriorityProbe.Record(message.Id, "late");
    return ValueTask.CompletedTask;
  }
}

/// <summary>No priority declared: runs at the default, between early and late.</summary>
public sealed class PrCommandDefaultAnswers : IReceptor<PrCommand, PrResult> {
  public ValueTask<PrResult> HandleAsync(PrCommand message, CancellationToken cancellationToken = default) {
    PriorityProbe.Record(message.Id, "default");
    return ValueTask.FromResult(new PrResult(message.Id, "default"));
  }
}

[ReceptorPriority(ReceptorPriority.EARLY)]
public sealed class PrCommandEarly : IReceptor<PrCommand> {
  public ValueTask HandleAsync(PrCommand message, CancellationToken cancellationToken = default) {
    PriorityProbe.Record(message.Id, "early");
    return ValueTask.CompletedTask;
  }
}

[ReceptorPriority(ReceptorPriority.LATE)]
public sealed class PrTwoAnswersLate : IReceptor<PrTwoAnswersCommand, PrResult> {
  public ValueTask<PrResult> HandleAsync(PrTwoAnswersCommand message, CancellationToken cancellationToken = default) {
    PriorityProbe.Record(message.Id, "late");
    return ValueTask.FromResult(new PrResult(message.Id, "late"));
  }
}

[ReceptorPriority(ReceptorPriority.EARLY)]
public sealed class PrTwoAnswersEarly : IReceptor<PrTwoAnswersCommand, PrResult> {
  public ValueTask<PrResult> HandleAsync(PrTwoAnswersCommand message, CancellationToken cancellationToken = default) {
    PriorityProbe.Record(message.Id, "early");
    return ValueTask.FromResult(new PrResult(message.Id, "early"));
  }
}

[ReceptorPriority(ReceptorPriority.LATE)]
public sealed class PrEventLate : IReceptor<PrEvent> {
  public ValueTask HandleAsync(PrEvent message, CancellationToken cancellationToken = default) {
    PriorityProbe.Record(message.Id, "late");
    return ValueTask.CompletedTask;
  }
}

public sealed class PrEventDefault : IReceptor<PrEvent> {
  public ValueTask HandleAsync(PrEvent message, CancellationToken cancellationToken = default) {
    PriorityProbe.Record(message.Id, "default");
    return ValueTask.CompletedTask;
  }
}

[ReceptorPriority(ReceptorPriority.EARLY)]
public sealed class PrEventEarly : IReceptor<PrEvent> {
  public ValueTask HandleAsync(PrEvent message, CancellationToken cancellationToken = default) {
    PriorityProbe.Record(message.Id, "early");
    return ValueTask.CompletedTask;
  }
}
