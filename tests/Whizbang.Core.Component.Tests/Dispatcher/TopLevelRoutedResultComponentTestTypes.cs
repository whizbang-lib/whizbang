// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;

// Types for TopLevelRoutedResultComponentTests (#1405). Each asking receptor returns its follow-up event wrapped
// directly in Route.Local or Route.Outbox, not inside a tuple. The source generator discovers every receptor here,
// so the tests run the REAL generated invokers, which used to strip the wrapper before the cascade read it.

namespace Whizbang.Core.Component.Tests.TopLevelRouted;

/// <summary>Which follow-up events ran here, per id. Each test uses a fresh id.</summary>
public static class RoutedProbe {
  private static readonly ConcurrentDictionary<Guid, int> _handledHere = new();

  public static void RecordHandledHere(Guid id) => _handledHere.AddOrUpdate(id, 1, static (_, n) => n + 1);

  public static int HandledHere(Guid id) => _handledHere.TryGetValue(id, out var n) ? n : 0;
}

public record TrKeepLocal(Guid Id) : ICommand;

public record TrSendOut(Guid Id) : ICommand;

public record TrAnnounceLocal([property: StreamId] Guid Id) : IEvent;

public record TrAnnounceOut([property: StreamId] Guid Id) : IEvent;

public record TrFollowUp([property: StreamId] Guid Id) : IEvent;

public sealed class TrKeepLocalReceptor : IReceptor<TrKeepLocal, Routed<TrFollowUp>> {
  public ValueTask<Routed<TrFollowUp>> HandleAsync(TrKeepLocal message, CancellationToken cancellationToken = default) =>
    ValueTask.FromResult(Route.Local(new TrFollowUp(message.Id)));
}

public sealed class TrSendOutReceptor : IReceptor<TrSendOut, Routed<TrFollowUp>> {
  public ValueTask<Routed<TrFollowUp>> HandleAsync(TrSendOut message, CancellationToken cancellationToken = default) =>
    ValueTask.FromResult(Route.Outbox(new TrFollowUp(message.Id)));
}

public sealed class TrAnnounceLocalReceptor : IReceptor<TrAnnounceLocal, Routed<TrFollowUp>> {
  public ValueTask<Routed<TrFollowUp>> HandleAsync(TrAnnounceLocal message, CancellationToken cancellationToken = default) =>
    ValueTask.FromResult(Route.Local(new TrFollowUp(message.Id)));
}

public sealed class TrAnnounceOutReceptor : IReceptor<TrAnnounceOut, Routed<TrFollowUp>> {
  public ValueTask<Routed<TrFollowUp>> HandleAsync(TrAnnounceOut message, CancellationToken cancellationToken = default) =>
    ValueTask.FromResult(Route.Outbox(new TrFollowUp(message.Id)));
}

public sealed class TrFollowUpReceptor : IReceptor<TrFollowUp> {
  public ValueTask HandleAsync(TrFollowUp message, CancellationToken cancellationToken = default) {
    RoutedProbe.RecordHandledHere(message.Id);
    return ValueTask.CompletedTask;
  }
}
