// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;

namespace Whizbang.Core.Data;

/// <summary>
/// Abstraction for splitting domain objects into JSONB columns for PostgreSQL persistence.
/// Hides the 3-column pattern (data/metadata/scope) from developers.
/// Implementations handle serialization and size calculation in C#.
/// </summary>
/// <typeparam name="T">The type to be persisted (event envelope or perspective model)</typeparam>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPostgresEventStoreTests.cs:CreateEventStoreAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPostgresEventStore.RetryTests.cs:SetupAsync</tests>
public interface IJsonbPersistenceAdapter<T> {
  /// <summary>
  /// Splits an object into JSONB-compatible components for persistence.
  /// Calculates sizes in C# (not stored unless threshold crossed).
  /// </summary>
  /// <param name="source">The object to split into JSONB columns</param>
  /// <param name="policyConfig">Optional policy configuration for size validation</param>
  /// <returns>JSONB persistence model with data/metadata/scope JSON strings</returns>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:AppendAsync_ShouldStoreEventAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:AppendAsync_DifferentStreams_ShouldBeIndependentAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:AppendAsync_ConcurrentAppends_ShouldBeThreadSafeAsync</tests>
  JsonbPersistenceModel ToJsonb(T source, PolicyConfiguration? policyConfig = null);

  /// <summary>
  /// Reconstructs an object from JSONB components.
  /// </summary>
  /// <param name="jsonb">The JSONB persistence model to reconstruct from</param>
  /// <returns>The reconstructed domain object</returns>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:ReadAsync_ShouldReturnEventsInOrderAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:ReadAsync_FromMiddle_ShouldReturnSubsetAsync</tests>
  T FromJsonb(JsonbPersistenceModel jsonb);

  /// <summary>
  /// Reconstructs an object from JSONB components with strong typing.
  /// Used for event stores where the message type needs to be specified for AOT-compatible deserialization.
  /// </summary>
  /// <typeparam name="TMessage">The message type to deserialize</typeparam>
  /// <param name="jsonb">The JSONB persistence model to reconstruct from</param>
  /// <returns>The reconstructed envelope with strongly-typed payload</returns>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:AppendAsync_ShouldStoreEventAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:ReadAsync_ShouldReturnEventsInOrderAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:ReadAsync_FromMiddle_ShouldReturnSubsetAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:ReadAsync_FromEmptyStream_ShouldReturnEmptyAsync</tests>
  MessageEnvelope<TMessage> FromJsonb<TMessage>(JsonbPersistenceModel jsonb);
}

/// <summary>
/// Container for the 3-column JSONB pattern.
/// Includes C#-calculated size properties (not stored unless threshold crossed).
/// </summary>
/// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:AppendAsync_ShouldStoreEventAsync</tests>
/// <tests>src/Whizbang.Testing/Contracts/EventStoreContractTests.cs:ReadAsync_ShouldReturnEventsInOrderAsync</tests>
public record JsonbPersistenceModel {
  /// <summary>
  /// The message id of a stored event in the Dapper store's form: its <c>message_id</c> key, or, when the row left it
  /// out because it equals the row's event id (#2323), the event id.
  /// </summary>
  /// <param name="metadata">The row's metadata keys.</param>
  /// <param name="eventId">The row's event id, when the read selected it.</param>
  /// <returns>The message id the event was written with.</returns>
  /// <exception cref="InvalidOperationException">The metadata has no message id and the read selected no event id.</exception>
  /// <docs>data/event-store#row-values</docs>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStoredMetadataRowValuesTests.cs:MessageIdOf_TheStoredKey_ElseTheRowsEventId_ElseTheRowCannotBeReadAsync</tests>
  public static Guid MessageIdOf(Dictionary<string, System.Text.Json.JsonElement> metadata, Guid? eventId) {
    ArgumentNullException.ThrowIfNull(metadata);
    if (metadata.TryGetValue("message_id", out var stored)) {
      return Guid.Parse(stored.GetString()!);
    }
    return eventId ?? throw new InvalidOperationException("message_id not found in metadata");
  }

  /// <summary>
  /// The row's event id (<c>wh_event_store.event_id</c>), when the read selected it: the message id of a row whose
  /// metadata left it out (#2323).
  /// </summary>
  /// <docs>data/event-store#row-values</docs>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStoredMetadataRowValuesTests.cs:EveryReader_ReadsATrimmedRow_AsItReadTheSameRowInTheSlimFormAsync</tests>
  public Guid? EventId { get; init; }

  /// <summary>
  /// The service the row's event was received from (<c>wh_event_store.origin_service_id</c>), when the read selected it;
  /// null for a local event. The envelope rebuilt from the row carries it as its
  /// <see cref="Observability.MessageEnvelope{TMessage}.SourceServiceId"/>, as the EF Core store's reads do (#2509).
  /// </summary>
  /// <docs>fundamentals/events/event-store#read-order</docs>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperEventOriginReadTests.cs:EveryReader_CarriesEachEventsOrigin_OntoItsEnvelopeAsync</tests>
  public Guid? OriginServiceId { get; init; }

  /// <summary>
  /// The row's event's position at the service it was received from (<c>wh_event_store.origin_commit_sequence</c>), when
  /// the read selected it; null for a local event. The envelope rebuilt from the row carries it as its
  /// <see cref="Observability.MessageEnvelope{TMessage}.SourceCommitSequence"/> (#2509).
  /// </summary>
  /// <docs>fundamentals/events/event-store#read-order</docs>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperEventOriginReadTests.cs:EveryReader_CarriesEachEventsOrigin_OntoItsEnvelopeAsync</tests>
  public long? OriginCommitSequence { get; init; }

  /// <summary>
  /// Event data or model data (3-6 KiB typical).
  /// </summary>
  public string DataJson { get; init; } = string.Empty;

  /// <summary>
  /// Metadata including correlation, causation, hops (800 bytes - 1.5 KiB typical).
  /// May contain __size_warning if threshold was crossed.
  /// </summary>
  public string MetadataJson { get; init; } = string.Empty;

  /// <summary>
  /// Scope information: tenant, user, org (200-500 bytes typical).
  /// Optional.
  /// </summary>
  public string? ScopeJson { get; init; }

  /// <summary>
  /// Calculated size of data JSON in bytes (C# calculation, not stored).
  /// </summary>
  public int DataSizeBytes => Encoding.UTF8.GetByteCount(DataJson);

  /// <summary>
  /// Calculated size of metadata JSON in bytes (C# calculation, not stored).
  /// </summary>
  public int MetadataSizeBytes => Encoding.UTF8.GetByteCount(MetadataJson);

  /// <summary>
  /// Calculated size of scope JSON in bytes (C# calculation, not stored).
  /// </summary>
  public int ScopeSizeBytes => ScopeJson != null ? Encoding.UTF8.GetByteCount(ScopeJson) : 0;

  /// <summary>
  /// Total calculated size across all JSONB columns (C# calculation, not stored).
  /// </summary>
  public int TotalSizeBytes => DataSizeBytes + MetadataSizeBytes + ScopeSizeBytes;
}
