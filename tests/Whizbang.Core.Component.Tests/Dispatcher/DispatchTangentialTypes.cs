// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Attributes;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;

// For DispatchTangentialParityComponentTests: receptors that report what the dispatch gave them.

namespace Whizbang.Core.Component.Tests.Tangential.Owned;

/// <summary>What each receptor saw, keyed by message id.</summary>
public static class TangentialProbe {
  private static readonly ConcurrentDictionary<(string, Guid), string?> _seen = new();

  public static void Saw(string what, Guid id, string? value) => _seen[(what, id)] = value;

  public static string? Seen(string what, Guid id) => _seen.TryGetValue((what, id), out var value) ? value : null;

  public static bool Ran(string what, Guid id) => _seen.ContainsKey((what, id));
}

public record TgTenantCommand(Guid Id) : ICommand;

public sealed class TgTenantCommandReceptor(IMessageContextAccessor contextAccessor) : IReceptor<TgTenantCommand> {
  public ValueTask HandleAsync(TgTenantCommand message, CancellationToken cancellationToken = default) {
    TangentialProbe.Saw("tenant", message.Id, contextAccessor.Current?.TenantId);
    return ValueTask.CompletedTask;
  }
}

public record TgTenantEvent([property: StreamId] Guid Id) : IEvent;

public sealed class TgTenantEventReceptor(IMessageContextAccessor contextAccessor) : IReceptor<TgTenantEvent> {
  public ValueTask HandleAsync(TgTenantEvent message, CancellationToken cancellationToken = default) {
    TangentialProbe.Saw("tenant", message.Id, contextAccessor.Current?.TenantId);
    return ValueTask.CompletedTask;
  }
}

/// <summary>A command whose receptor looks up the envelope it is handling, as an event-store append does.</summary>
public record TgEnvelopeCommand(Guid Id) : ICommand;

public sealed class TgEnvelopeCommandReceptor(IEnvelopeRegistry registry) : IReceptor<TgEnvelopeCommand> {
  public ValueTask HandleAsync(TgEnvelopeCommand message, CancellationToken cancellationToken = default) {
    TangentialProbe.Saw("envelope", message.Id, registry.TryGetEnvelope(message)?.MessageId.ToString());
    return ValueTask.CompletedTask;
  }
}

public record TgEnvelopeEvent([property: StreamId] Guid Id) : IEvent;

public sealed class TgEnvelopeEventReceptor(IEnvelopeRegistry registry) : IReceptor<TgEnvelopeEvent> {
  public ValueTask HandleAsync(TgEnvelopeEvent message, CancellationToken cancellationToken = default) {
    TangentialProbe.Saw("envelope", message.Id, registry.TryGetEnvelope(message)?.MessageId.ToString());
    return ValueTask.CompletedTask;
  }
}

/// <summary>An event whose dispatch stamps its sent time and correlation onto it, as a consumer's base event declares.</summary>
public sealed class TgStampedEvent : IEvent {
  [StreamId]
  public Guid Id { get; init; }

  [PopulateTimestamp(TimestampKind.SentAt)]
  public DateTimeOffset SentAt { get; set; }

  [PopulateFromIdentifier(IdentifierKind.CorrelationId)]
  public string? CorrelationId { get; set; }
}

public sealed class TgStampedEventReceptor(IEnvelopeRegistry registry) : IReceptor<TgStampedEvent> {
  public ValueTask HandleAsync(TgStampedEvent message, CancellationToken cancellationToken = default) {
    TangentialProbe.Saw("sentAt", message.Id, message.SentAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    TangentialProbe.Saw("correlation", message.Id, message.CorrelationId);
    TangentialProbe.Saw("envelopeId", message.Id, registry.TryGetEnvelope(message)?.MessageId.Value.ToString());
    return ValueTask.CompletedTask;
  }
}

/// <summary>A command whose receptor returns a stamped event, so the event cascades from the result.</summary>
public record TgStampingCommand(Guid Id) : ICommand;

public sealed class TgStampingCommandReceptor : IReceptor<TgStampingCommand, TgStampedEvent> {
  public ValueTask<TgStampedEvent> HandleAsync(TgStampingCommand message, CancellationToken cancellationToken = default) =>
    ValueTask.FromResult(new TgStampedEvent { Id = message.Id });
}

/// <summary>An event whose stream id the dispatcher generates when it is empty.</summary>
[GenerateStreamId]
public sealed class TgGeneratedStreamEvent : IEvent {
  [StreamId]
  public Guid StreamId { get; set; }

  public required Guid Marker { get; init; }
}

public sealed class TgGeneratedStreamEventReceptor : IReceptor<TgGeneratedStreamEvent> {
  public ValueTask HandleAsync(TgGeneratedStreamEvent message, CancellationToken cancellationToken = default) {
    TangentialProbe.Saw("stream", message.Marker, message.StreamId.ToString());
    return ValueTask.CompletedTask;
  }
}

/// <summary>An unowned-looking plain command used where a copy must go out; owned here, so sends of it stay.</summary>
public record TgVoidCommand(Guid Id) : ICommand;

public sealed class TgVoidCommandReceptor : IReceptor<TgVoidCommand> {
  public ValueTask HandleAsync(TgVoidCommand message, CancellationToken cancellationToken = default) {
    TangentialProbe.Saw("ran", message.Id, "yes");
    return ValueTask.CompletedTask;
  }
}

public record TgVoidEvent([property: StreamId] Guid Id) : IEvent;

public sealed class TgVoidEventReceptor : IReceptor<TgVoidEvent> {
  public ValueTask HandleAsync(TgVoidEvent message, CancellationToken cancellationToken = default) {
    TangentialProbe.Saw("ran", message.Id, "yes");
    return ValueTask.CompletedTask;
  }
}

/// <summary>An owned command whose receptor publishes an event and then fails: its batch stops before any copy.</summary>
public record TgEmitThenFailCommand(Guid Id) : ICommand;

public sealed class TgEmitThenFailCommandReceptor(IDispatcher dispatcher) : IReceptor<TgEmitThenFailCommand> {
  public async ValueTask HandleAsync(TgEmitThenFailCommand message, CancellationToken cancellationToken = default) {
    await dispatcher.PublishAsync(new TgVoidEvent(message.Id));
    throw new InvalidOperationException("failed after emitting");
  }
}

/// <summary>An owned command whose receptor fails without emitting anything.</summary>
public record TgFailCommand(Guid Id) : ICommand;

public sealed class TgFailCommandReceptor : IReceptor<TgFailCommand> {
  public ValueTask HandleAsync(TgFailCommand message, CancellationToken cancellationToken = default) =>
    throw new InvalidOperationException("failed");
}

/// <summary>An event whose receptor publishes a follow-up event: a cause and its effect.</summary>
public record TgCauseEvent([property: StreamId] Guid Id) : IEvent;

public sealed class TgCauseEventReceptor(IDispatcher dispatcher) : IReceptor<TgCauseEvent> {
  public async ValueTask HandleAsync(TgCauseEvent message, CancellationToken cancellationToken = default) =>
    await dispatcher.PublishAsync(new TgVoidEvent(message.Id));
}
