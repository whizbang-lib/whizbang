// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Routing.ExcludedEventFixtures;

/// <summary>A top-level event the excluded-event-types tests opt out of (#2175).</summary>
public sealed record NoisyEvent([property: StreamId] Guid StreamId) : IEvent;

/// <summary>A second top-level event on the same topic as <see cref="NoisyEvent"/>.</summary>
public sealed record ChattyEvent([property: StreamId] Guid StreamId) : IEvent;

/// <summary>Holds a nested event: a top-level type hides every naming bug a nested one exposes.</summary>
public static class Container {
  /// <summary>A nested event the excluded-event-types tests opt out of.</summary>
  public sealed record NestedNoisyEvent([property: StreamId] Guid StreamId) : IEvent;
}

/// <summary>A consumer registry answering from fixed lists, for the startup checks.</summary>
internal sealed class FixedConsumerRegistry(
    IReadOnlyList<ConsumedMessageInfo>? consumed = null,
    Func<string, bool>? predicate = null) : IConsumerRegistry {
  public bool HasAnyConsumer(string messageType) => predicate?.Invoke(messageType) ?? false;

  public IReadOnlyList<ConsumedMessageInfo> GetConsumedMessages() => consumed ?? [];
}

/// <summary>A message catalog answering from a fixed list.</summary>
internal sealed class FixedMessageCatalog(bool available, params Type[] types) : IMessageTypeCatalog {
  public bool IsAvailable => available;

  public IReadOnlyList<MessageTypeCatalogEntry> GetAll() =>
    [.. types.Select(t => new MessageTypeCatalogEntry(t, TypeNameFormatter.FormatClrTypeName(t), "event", null))];
}

/// <summary>A logger recording every entry, every level enabled.</summary>
internal sealed class RecordingRoutingLogger : ILogger {
  public List<(LogLevel Level, string Message)> Entries { get; } = [];

  public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

  public bool IsEnabled(LogLevel logLevel) => true;

  public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
      Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));

  public bool Has(LogLevel level, string fragment) =>
    Entries.Exists(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal));
}
