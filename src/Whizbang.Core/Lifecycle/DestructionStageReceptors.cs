// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.DependencyInjection;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// Runs the receptors declared at the destruction lifecycle stages (#1381): <see cref="LifecycleStage.PreDestructionDetached"/>
/// and <see cref="LifecycleStage.PreDestructionInline"/> before an event's data is deleted, and
/// <see cref="LifecycleStage.PostDestructionDetached"/> and <see cref="LifecycleStage.PostDestructionInline"/> after the
/// delete commits. Before this, those stages could be declared and registered but nothing ran them. Both operations that
/// destroy stored events use it: the ephemeral reap and a period close (#1504).
/// </summary>
/// <remarks>
/// <para>Receptors are typed by message, so the events about to be deleted are loaded before the delete and the same
/// envelopes are handed to the Post stages, when the stored data is already gone. Only events whose type has a receptor
/// at a destruction stage are loaded; with none registered, nothing is read at all.</para>
/// <para>Each Detached stage runs in the background in its own scope; each Inline stage is awaited, so a throwing Pre
/// Inline receptor fails the destruction exactly as a throwing destruction hook does.</para>
/// </remarks>
/// <docs>fundamentals/lifecycle/lifecycle-stages#destruction</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/DestructionStageReceptorComponentTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Lifecycle/StreamPrefixTruncatorDestructionStageComponentTests.cs</tests>
public static partial class DestructionStageReceptors {
  private static readonly LifecycleStage[] _stages = [
    LifecycleStage.PreDestructionDetached, LifecycleStage.PreDestructionInline,
    LifecycleStage.PostDestructionDetached, LifecycleStage.PostDestructionInline,
  ];

  /// <summary>The event types that have a receptor at any destruction stage, resolved by stored type name.</summary>
  /// <param name="services">The cycle's scoped provider.</param>
  /// <returns>A lookup from stored type name to type, empty when no destruction-stage receptor exists.</returns>
  public static Dictionary<string, Type> TypesWithReceptors(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    if (services.GetService<IReceptorRegistry>() is not { } registry || services.GetService<IMessageTypeCatalog>() is not { } catalog) {
      return [];
    }
    var types = catalog.GetAll()
      .Select(e => e.Type)
      .Where(t => Array.Exists(_stages, stage => registry.GetReceptorsFor(t, stage).Count > 0))
      .ToList();
    return types.Count == 0 ? [] : EventTypeMatchingHelper.BuildTypeLookup(types);
  }

  /// <summary>
  /// Loads, before the delete, the events about to be destroyed whose type has a destruction-stage receptor.
  /// </summary>
  /// <param name="services">The cycle's scoped provider.</param>
  /// <param name="targets">The events about to be destroyed.</param>
  /// <param name="typesWithReceptors">From <see cref="TypesWithReceptors"/>.</param>
  /// <param name="cancellationToken">The cycle's token.</param>
  /// <returns>The loaded envelopes; empty when none has a receptor.</returns>
  public static async Task<IReadOnlyList<MessageEnvelope<IEvent>>> LoadAsync(IServiceProvider services,
      IReadOnlyList<EphemeralDestructionTarget> targets, Dictionary<string, Type> typesWithReceptors, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(targets);
    ArgumentNullException.ThrowIfNull(typesWithReceptors);
    if (typesWithReceptors.Count == 0 || services.GetService<IEventStore>() is not { } eventStore) {
      return [];
    }
    var loaded = new List<MessageEnvelope<IEvent>>();
    foreach (var stream in targets.GroupBy(t => t.StreamId)) {
      var wanted = new HashSet<Guid>();
      var types = new HashSet<Type>();
      foreach (var target in stream) {
        if (EventTypeMatchingHelper.TryResolveType(typesWithReceptors, target.EventType, out var type)) {
          wanted.Add(target.EventId);
          types.Add(type);
        }
      }
      if (wanted.Count == 0) {
        continue;
      }
      await foreach (var envelope in eventStore.ReadPolymorphicAsync(stream.Key, fromEventId: null, [.. types], cancellationToken).ConfigureAwait(false)) {
        if (wanted.Contains(envelope.MessageId.Value)) {
          loaded.Add(envelope);
        }
      }
    }
    return loaded;
  }

  /// <summary>Runs the Pre stages for each envelope: Detached in the background, then Inline, awaited.</summary>
  /// <param name="services">The cycle's scoped provider.</param>
  /// <param name="envelopes">From <see cref="LoadAsync"/>.</param>
  /// <param name="cancellationToken">The cycle's token.</param>
  /// <returns>A task that completes when every Inline stage has run.</returns>
  public static Task FirePreAsync(IServiceProvider services, IReadOnlyList<MessageEnvelope<IEvent>> envelopes, CancellationToken cancellationToken) =>
    _fireAsync(services, envelopes, LifecycleStage.PreDestructionDetached, LifecycleStage.PreDestructionInline, cancellationToken);

  /// <summary>Runs the Post stages for each envelope, after the delete committed: Detached in the background, then Inline.</summary>
  /// <param name="services">The cycle's scoped provider.</param>
  /// <param name="envelopes">The envelopes <see cref="LoadAsync"/> returned before the delete.</param>
  /// <param name="cancellationToken">The cycle's token.</param>
  /// <returns>A task that completes when every Inline stage has run.</returns>
  public static Task FirePostAsync(IServiceProvider services, IReadOnlyList<MessageEnvelope<IEvent>> envelopes, CancellationToken cancellationToken) =>
    _fireAsync(services, envelopes, LifecycleStage.PostDestructionDetached, LifecycleStage.PostDestructionInline, cancellationToken);

  private static async Task _fireAsync(IServiceProvider services, IReadOnlyList<MessageEnvelope<IEvent>> envelopes,
      LifecycleStage detached, LifecycleStage inline, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(envelopes);
    if (envelopes.Count == 0) {
      return;
    }
    var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
    var invoker = services.GetService<IReceptorInvoker>();
    foreach (var envelope in envelopes) {
      var context = new LifecycleExecutionContext {
        CurrentStage = inline,
        EventId = envelope.MessageId.Value,
        MessageSource = MessageSource.Local,
      };
      _ = _fireDetachedAsync(scopeFactory, envelope, detached, context with { CurrentStage = detached });
      if (invoker is not null) {
        await invoker.InvokeAsync(envelope, inline, context, cancellationToken).ConfigureAwait(false);
      }
    }
  }

  private static Task _fireDetachedAsync(IServiceScopeFactory scopeFactory, MessageEnvelope<IEvent> envelope, LifecycleStage stage,
      LifecycleExecutionContext context) =>
    Task.Run(async () => {
      try {
        await using var scope = scopeFactory.CreateAsyncScope();
        await SecurityContextHelper.EstablishFullContextAsync(envelope, scope.ServiceProvider, default).ConfigureAwait(false);
        if (scope.ServiceProvider.GetService<IReceptorInvoker>() is { } invoker) {
          await invoker.InvokeAsync(envelope, stage, context, default).ConfigureAwait(false);
        }
      } catch (OperationCanceledException) {
        // Shutdown
      } catch (Exception ex) {
        using var logScope = scopeFactory.CreateScope();
        LogDetachedStageFailed(logScope.ServiceProvider.GetLoggerOrNullLogger("Whizbang.Core.Lifecycle.DestructionStages"), ex, stage, envelope.MessageId.Value);
      }
    });

  [LoggerMessage(Level = LogLevel.Error, Message = "Destruction stage {Stage} failed for event {EventId}")]
  private static partial void LogDetachedStageFailed(ILogger logger, Exception exception, LifecycleStage stage, Guid eventId);
}
