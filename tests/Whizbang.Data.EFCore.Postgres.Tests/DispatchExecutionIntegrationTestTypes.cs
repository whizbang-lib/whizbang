// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

// Types for DispatchExecutionIntegrationTests (#1410 follow-up). Commands in the Owned namespace belong
// to the service; everything in Upstream belongs to another. The source generator discovers every
// receptor here, so the tests run the REAL generated lookup tables and receptor registry.

namespace Whizbang.Data.EFCore.Postgres.Tests.DispatchExecution {
  /// <summary>Invocation counts per (receptor, message id). Each test uses fresh ids.</summary>
  public static class DispatchExecutionProbe {
    private static readonly ConcurrentDictionary<(string Receptor, Guid Id), int> _counts = new();

    public static void Record(string receptor, Guid id) => _counts.AddOrUpdate((receptor, id), 1, static (_, n) => n + 1);

    public static int Count(string receptor, Guid id) => _counts.TryGetValue((receptor, id), out var n) ? n : 0;
  }

  /// <summary>
  /// Runs per (service, message id) for the round-trip receptors, with a signal per pair, so a test can
  /// tell the sender's run from a receiver's and wait for the one it expects.
  /// </summary>
  public static class RoundTripProbe {
    private static readonly ConcurrentDictionary<(string Service, Guid Id), int> _counts = new();
    private static readonly ConcurrentDictionary<(string Service, Guid Id), TaskCompletionSource> _ran = new();

    public static void Record(string service, Guid id) {
      _counts.AddOrUpdate((service, id), 1, static (_, n) => n + 1);
      Ran(service, id).TrySetResult();
    }

    public static int Count(string service, Guid id) => _counts.TryGetValue((service, id), out var n) ? n : 0;

    public static TaskCompletionSource Ran(string service, Guid id) =>
      _ran.GetOrAdd((service, id), static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
  }

  /// <summary>A message the runtime-registered counting handler can key by id.</summary>
  public interface IDxIdentified {
    Guid Id { get; }
  }
}

namespace Whizbang.Data.EFCore.Postgres.Tests.DispatchExecution.Owned {
  public sealed record DxOwnedCommand(Guid Id) : ICommand;

  public sealed class DxOwnedCommandReceptor : IReceptor<DxOwnedCommand> {
    public ValueTask HandleAsync(DxOwnedCommand message, CancellationToken cancellationToken = default) {
      DispatchExecutionProbe.Record(nameof(DxOwnedCommandReceptor), message.Id);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>An owned command whose receptor returns an event: the shape #1410 stopped running.</summary>
  public sealed record DxOwnedResultCommand(Guid Id) : ICommand;

  public sealed class DxOwnedResultCommandReceptor : IReceptor<DxOwnedResultCommand, Upstream.DxFollowUpEvent> {
    public ValueTask<Upstream.DxFollowUpEvent> HandleAsync(DxOwnedResultCommand message, CancellationToken cancellationToken = default) {
      DispatchExecutionProbe.Record(nameof(DxOwnedResultCommandReceptor), message.Id);
      return ValueTask.FromResult(new Upstream.DxFollowUpEvent(message.Id));
    }
  }

  public sealed record DxOwnedUnhandledCommand(Guid Id) : ICommand;

  /// <summary>
  /// A cause whose handler returns its effect on the same stream, routed as the cause says: the effect
  /// must be stored after the cause whichever way it is routed.
  /// </summary>
  public sealed record DxCauseEvent([property: StreamId] Guid Id, string EffectRoute) : IEvent;

  public sealed record DxEffectEvent([property: StreamId] Guid Id) : IEvent;

  public sealed class DxCauseEventReceptor : IReceptor<DxCauseEvent, Routed<DxEffectEvent>> {
    public ValueTask<Routed<DxEffectEvent>> HandleAsync(DxCauseEvent message, CancellationToken cancellationToken = default) {
      var effect = new DxEffectEvent(message.Id);
      return ValueTask.FromResult(message.EffectRoute switch {
        "Local" => Route.Local(effect),
        "EventStoreOnly" => Route.EventStoreOnly(effect),
        _ => Route.Both(effect),
      });
    }
  }

  /// <summary>An event the sender owns and handles, delivered back to itself or on to another service.</summary>
  public sealed record DxRoundTripEvent([property: StreamId] Guid Id) : IEvent;

  public sealed class DxRoundTripEventReceptor(IServiceInstanceProvider instance) : IReceptor<DxRoundTripEvent> {
    public ValueTask HandleAsync(DxRoundTripEvent message, CancellationToken cancellationToken = default) {
      RoundTripProbe.Record(instance.ServiceName, message.Id);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// Handled only by receptors the test registers at runtime, so it can count the PreOutbox stage. In the
  /// owned namespace: with owned domains configured, PreOutbox runs for the service's own events only.
  /// </summary>
  public sealed record DxRuntimeHandledEvent([property: StreamId] Guid Id) : IEvent, IDxIdentified;
}

namespace Whizbang.Data.EFCore.Postgres.Tests.DispatchExecution.Upstream {
  public sealed record DxUnownedCommand(Guid Id) : ICommand;

  /// <summary>A command another service owns that the sender also handles, delivered back or on.</summary>
  public sealed record DxRoundTripCommand(Guid Id) : ICommand;

  public sealed class DxRoundTripCommandReceptor(IServiceInstanceProvider instance) : IReceptor<DxRoundTripCommand> {
    public ValueTask HandleAsync(DxRoundTripCommand message, CancellationToken cancellationToken = default) {
      RoundTripProbe.Record(instance.ServiceName, message.Id);
      return ValueTask.CompletedTask;
    }
  }

  public sealed class DxUnownedCommandReceptor : IReceptor<DxUnownedCommand> {
    public ValueTask HandleAsync(DxUnownedCommand message, CancellationToken cancellationToken = default) {
      DispatchExecutionProbe.Record(nameof(DxUnownedCommandReceptor), message.Id);
      return ValueTask.CompletedTask;
    }
  }

  public sealed record DxEvent([property: StreamId] Guid Id) : IEvent;

  public sealed class DxEventReceptor : IReceptor<DxEvent> {
    public ValueTask HandleAsync(DxEvent message, CancellationToken cancellationToken = default) {
      DispatchExecutionProbe.Record(nameof(DxEventReceptor), message.Id);
      return ValueTask.CompletedTask;
    }
  }

  public sealed record DxFollowUpEvent([property: StreamId] Guid Id) : IEvent;

  public sealed class DxFollowUpEventReceptor : IReceptor<DxFollowUpEvent> {
    public ValueTask HandleAsync(DxFollowUpEvent message, CancellationToken cancellationToken = default) {
      DispatchExecutionProbe.Record(nameof(DxFollowUpEventReceptor), message.Id);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Handled only by receptors the test registers at runtime, so it can count the PreOutbox stage.</summary>
  public sealed record DxRuntimeHandledCommand(Guid Id) : ICommand, IDxIdentified;

}
