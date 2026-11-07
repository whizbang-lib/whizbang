// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// How the EF Core driver's direct append (<see cref="EFCoreEventStore{TDbContext}.AppendAsync{TMessage}(Guid, TMessage, CancellationToken)"/>)
/// stores an event, for the book closer (#1741): the same minimal envelope, the payload serialized as its declared type,
/// the same envelope metadata, no scope column, and the same kind bits. The metadata is written as JSON text with the
/// options the driver's data source maps it with.
/// </summary>
/// <param name="jsonOptions">The options the driver serializes with (<c>JsonContextRegistry.CreateCombinedOptions()</c>).</param>
/// <param name="ephemeralModeResolver">Resolves an event type's ephemeral mode, as the direct append does; null for none.</param>
/// <docs>fundamentals/events/closing-the-books</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CloseBooksSqlTests.cs</tests>
public sealed class EFCoreDirectAppendRowBuilder(JsonSerializerOptions jsonOptions, IEphemeralModeResolver? ephemeralModeResolver)
    : IDirectAppendRowBuilder {
  private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));

  /// <inheritdoc />
  public ValueTask<DirectAppendRow> BuildAsync<TMessage>(TMessage payload, CancellationToken cancellationToken = default) where TMessage : notnull {
    var envelope = EFCoreEventStoreRows.MinimalEnvelope(payload);
    var metadata = EFCoreEventStoreRows.MetadataOf(envelope);
    return ValueTask.FromResult(new DirectAppendRow(
      envelope.MessageId.Value,
      TypeNameFormatter.FormatClrTypeName(typeof(TMessage)),
      TypeNameFormatter.Format(typeof(TMessage)),
      JsonSerializer.Serialize(payload, (JsonTypeInfo<TMessage>)_jsonOptions.GetTypeInfo(typeof(TMessage))),
      JsonSerializer.Serialize(metadata, (JsonTypeInfo<EnvelopeMetadata>)_jsonOptions.GetTypeInfo(typeof(EnvelopeMetadata))),
      Scope: null,
      DirectAppendKind.Of(payload, ephemeralModeResolver)));
  }
}

/// <summary>The pieces of the EF Core direct append that the book closer stores the same way (#1741).</summary>
internal static class EFCoreEventStoreRows {
  /// <summary>The envelope a direct append of a bare message is stored with.</summary>
  internal static MessageEnvelope<TMessage> MinimalEnvelope<TMessage>(TMessage message) =>
    new() {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Timestamp = DateTimeOffset.UtcNow,
          TraceParent = System.Diagnostics.Activity.Current?.Id
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

  /// <summary>The envelope metadata a direct append stores.</summary>
  internal static EnvelopeMetadata MetadataOf<TMessage>(MessageEnvelope<TMessage> envelope) =>
    new() {
      MessageId = envelope.MessageId,
      Hops = envelope.Hops?.ToList() ?? [],
      // #1729: wh_append_event stamps a superseding event's key from the metadata it stores, and (#2124) its group.
      SupersedingKey = Whizbang.Core.Lifecycle.SupersedingEvents.KeyOf(envelope.Payload),
      SupersedingGroup = Whizbang.Core.Lifecycle.SupersedingEvents.GroupOf(envelope.Payload)
    };
}
