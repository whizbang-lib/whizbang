// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Attributes;
using Whizbang.Core.Observability;

// For DispatchSurfaceDerivedIdentityComponentTests: receptors that report the id of the envelope they handle (#2543).

namespace Whizbang.Core.Component.Tests.DerivedSurface.Owned;

/// <summary>The envelope ids each probe receptor looked up, keyed by the message's key.</summary>
public static class DerivedSurfaceProbe {
  private static readonly ConcurrentDictionary<Guid, ConcurrentQueue<Guid>> _seen = new();

  /// <summary>Records the id of the envelope registered for a message, when one is.</summary>
  public static void Saw(Guid key, IMessageEnvelope? envelope) {
    if (envelope is not null) {
      _seen.GetOrAdd(key, static _ => new ConcurrentQueue<Guid>()).Enqueue(envelope.MessageId.Value);
    }
  }

  /// <summary>The envelope ids recorded for <paramref name="key"/>, in the order the receptors saw them.</summary>
  public static IReadOnlyList<Guid> Seen(Guid key) => _seen.TryGetValue(key, out var ids) ? [.. ids] : [];
}

/// <summary>A command handled here that answers.</summary>
public record DsCommand(Guid Key) : ICommand;

/// <summary>The command's answer.</summary>
public record DsAnswer(Guid Key);

/// <summary>Records the envelope its command arrived in, and answers.</summary>
public sealed class DsCommandReceptor(IEnvelopeRegistry registry) : IReceptor<DsCommand, DsAnswer> {
  /// <inheritdoc />
  public ValueTask<DsAnswer> HandleAsync(DsCommand message, CancellationToken cancellationToken = default) {
    DerivedSurfaceProbe.Saw(message.Key, registry.TryGetEnvelope(message));
    return ValueTask.FromResult(new DsAnswer(message.Key));
  }
}

/// <summary>An event handled here.</summary>
public record DsEvent([property: StreamId] Guid Key) : IEvent;

/// <summary>Records the envelope its event arrived in.</summary>
public sealed class DsEventReceptor(IEnvelopeRegistry registry) : IReceptor<DsEvent> {
  /// <inheritdoc />
  public ValueTask HandleAsync(DsEvent message, CancellationToken cancellationToken = default) {
    DerivedSurfaceProbe.Saw(message.Key, registry.TryGetEnvelope(message));
    return ValueTask.CompletedTask;
  }
}
