// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

// Types for PostLifecycleOncePerHandlingServiceComponentTests (#1413). Each message has a business
// receptor and a receptor at each PostLifecycle stage that counts its invocations per message id. The
// counters are [ReceptorIdempotent], so the double-fire guard lets every invocation through: the counts
// are the calls the framework makes. The source generator discovers every receptor here, so the tests
// run the REAL generated lookups and receptor registry.

namespace Whizbang.Core.Component.Tests.PostLifecycleCounts;

/// <summary>Invocation counts per message id. Each test uses a fresh id.</summary>
public static class PostLifecycleProbe {
  private static readonly ConcurrentDictionary<Guid, int> _handled = new();
  private static readonly ConcurrentDictionary<Guid, int> _postLifecycleDetached = new();
  private static readonly ConcurrentDictionary<Guid, int> _postLifecycleInline = new();

  public static void RecordHandled(Guid id) => _handled.AddOrUpdate(id, 1, static (_, n) => n + 1);
  private static readonly ConcurrentDictionary<Guid, TaskCompletionSource> _detachedSignals = new();

  public static void RecordDetached(Guid id) {
    _postLifecycleDetached.AddOrUpdate(id, 1, static (_, n) => n + 1);
    _detachedSignal(id).TrySetResult();
  }

  /// <summary>
  /// Completes when PostLifecycleDetached has run for <paramref name="id"/>. With a lifecycle coordinator
  /// the Detached stage runs in the background, so a test waits on this signal rather than reading the
  /// count as soon as the dispatch returns.
  /// </summary>
  public static Task DetachedRanAsync(Guid id) => _detachedSignal(id).Task;

  private static TaskCompletionSource _detachedSignal(Guid id) =>
    _detachedSignals.GetOrAdd(id, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
  public static void RecordInline(Guid id) => _postLifecycleInline.AddOrUpdate(id, 1, static (_, n) => n + 1);

  public static int HandledCount(Guid id) => _handled.TryGetValue(id, out var n) ? n : 0;
  public static int DetachedCount(Guid id) => _postLifecycleDetached.TryGetValue(id, out var n) ? n : 0;
  public static int InlineCount(Guid id) => _postLifecycleInline.TryGetValue(id, out var n) ? n : 0;
}

public record PlCommand(Guid Id) : ICommand;

public record PlEvent([property: StreamId] Guid Id) : IEvent;

/// <summary>A command whose receptor returns <see cref="PlCascadedEvent"/>, which cascades.</summary>
public record PlCascadingCommand(Guid Id) : ICommand;

public record PlCascadedEvent([property: StreamId] Guid Id) : IEvent;

/// <summary>An event a perspective on this service applies (see <see cref="PlPerspectiveRegistry"/>).</summary>
public record PlProjectedEvent([property: StreamId] Guid Id) : IEvent;

public sealed class PlCommandReceptor : IReceptor<PlCommand> {
  public ValueTask HandleAsync(PlCommand message, CancellationToken cancellationToken = default) {
    PostLifecycleProbe.RecordHandled(message.Id);
    return ValueTask.CompletedTask;
  }
}

public sealed class PlEventReceptor : IReceptor<PlEvent> {
  public ValueTask HandleAsync(PlEvent message, CancellationToken cancellationToken = default) {
    PostLifecycleProbe.RecordHandled(message.Id);
    return ValueTask.CompletedTask;
  }
}

public sealed class PlCascadingCommandReceptor : IReceptor<PlCascadingCommand, PlCascadedEvent> {
  public ValueTask<PlCascadedEvent> HandleAsync(PlCascadingCommand message, CancellationToken cancellationToken = default) =>
    ValueTask.FromResult(new PlCascadedEvent(message.Id));
}

public sealed class PlCascadedEventReceptor : IReceptor<PlCascadedEvent> {
  public ValueTask HandleAsync(PlCascadedEvent message, CancellationToken cancellationToken = default) {
    PostLifecycleProbe.RecordHandled(message.Id);
    return ValueTask.CompletedTask;
  }
}

public sealed class PlProjectedEventReceptor : IReceptor<PlProjectedEvent> {
  public ValueTask HandleAsync(PlProjectedEvent message, CancellationToken cancellationToken = default) {
    PostLifecycleProbe.RecordHandled(message.Id);
    return ValueTask.CompletedTask;
  }
}

[FireAt(LifecycleStage.PostLifecycleDetached)]
[ReceptorIdempotent]
public sealed class PlCommandDetachedCounter : IReceptor<PlCommand> {
  public ValueTask HandleAsync(PlCommand message, CancellationToken cancellationToken = default) {
    PostLifecycleProbe.RecordDetached(message.Id);
    return ValueTask.CompletedTask;
  }
}

[FireAt(LifecycleStage.PostLifecycleInline)]
[ReceptorIdempotent]
public sealed class PlCommandInlineCounter : IReceptor<PlCommand> {
  public ValueTask HandleAsync(PlCommand message, CancellationToken cancellationToken = default) {
    PostLifecycleProbe.RecordInline(message.Id);
    return ValueTask.CompletedTask;
  }
}

[FireAt(LifecycleStage.PostLifecycleDetached)]
[ReceptorIdempotent]
public sealed class PlEventDetachedCounter : IReceptor<PlEvent> {
  public ValueTask HandleAsync(PlEvent message, CancellationToken cancellationToken = default) {
    PostLifecycleProbe.RecordDetached(message.Id);
    return ValueTask.CompletedTask;
  }
}

[FireAt(LifecycleStage.PostLifecycleInline)]
[ReceptorIdempotent]
public sealed class PlEventInlineCounter : IReceptor<PlEvent> {
  public ValueTask HandleAsync(PlEvent message, CancellationToken cancellationToken = default) {
    PostLifecycleProbe.RecordInline(message.Id);
    return ValueTask.CompletedTask;
  }
}

[FireAt(LifecycleStage.PostLifecycleDetached)]
[ReceptorIdempotent]
public sealed class PlCascadedEventDetachedCounter : IReceptor<PlCascadedEvent> {
  public ValueTask HandleAsync(PlCascadedEvent message, CancellationToken cancellationToken = default) {
    PostLifecycleProbe.RecordDetached(message.Id);
    return ValueTask.CompletedTask;
  }
}

[FireAt(LifecycleStage.PostLifecycleInline)]
[ReceptorIdempotent]
public sealed class PlCascadedEventInlineCounter : IReceptor<PlCascadedEvent> {
  public ValueTask HandleAsync(PlCascadedEvent message, CancellationToken cancellationToken = default) {
    PostLifecycleProbe.RecordInline(message.Id);
    return ValueTask.CompletedTask;
  }
}

[FireAt(LifecycleStage.PostLifecycleDetached)]
[ReceptorIdempotent]
public sealed class PlProjectedEventDetachedCounter : IReceptor<PlProjectedEvent> {
  public ValueTask HandleAsync(PlProjectedEvent message, CancellationToken cancellationToken = default) {
    PostLifecycleProbe.RecordDetached(message.Id);
    return ValueTask.CompletedTask;
  }
}

[FireAt(LifecycleStage.PostLifecycleInline)]
[ReceptorIdempotent]
public sealed class PlProjectedEventInlineCounter : IReceptor<PlProjectedEvent> {
  public ValueTask HandleAsync(PlProjectedEvent message, CancellationToken cancellationToken = default) {
    PostLifecycleProbe.RecordInline(message.Id);
    return ValueTask.CompletedTask;
  }
}

/// <summary>
/// A perspective registry naming <see cref="PlProjectedEvent"/> as an event a perspective on this service
/// applies. The perspective worker fires PostLifecycle for such an event once it is stored, so a local run
/// that also stores it leaves PostLifecycle to the worker.
/// </summary>
public sealed class PlPerspectiveRegistry : IPerspectiveRunnerRegistry {
  public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
  public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [];
  public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  public IReadOnlyList<Type> GetEventTypes() => [typeof(PlProjectedEvent)];
}
