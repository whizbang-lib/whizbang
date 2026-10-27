// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// WHIZ133 (#1546) through the Dapper coordinator: the store refuses an event that would make a stream hold both
/// ephemeral and Sourced events. A write whose caller awaits it fails with <see cref="MixedStreamEventException"/>;
/// any other write dead-letters only the refused row. The SQL behavior itself is covered by the EF Core
/// <c>MixedStreamGuardSqlTests</c>; these tests lock this coordinator's side of it.
/// </summary>
/// <docs>operations/diagnostics/whiz133</docs>
public class DapperMixedStreamGuardTests : PostgresTestBase {
  private const string EPHEMERAL_TYPE = "Probe.Contracts.UserIsTyping, Probe.Contracts";
  private const string SOURCED_TYPE = "Probe.Contracts.OrderPlaced, Probe.Contracts";

  private readonly JsonSerializerOptions _jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

  [Test]
  public async Task StoreOutboxMessagesAsync_DirectCommitOfASourcedEventOnAnEphemeralStream_ThrowsMixedStreamEventExceptionAsync() {
    var coordinator = _build();
    var streamId = (Guid)TrackedGuid.New();
    await coordinator.StoreOutboxMessagesAsync([_event(streamId, EPHEMERAL_TYPE, EventFlags.Ephemeral)], partitionCount: 10000);
    var sourced = _event(streamId, SOURCED_TYPE, EventFlags.None) with { RaiseOnMixedStream = true };

    var thrown = await Assert.That(async () =>
        await coordinator.StoreOutboxMessagesAsync([sourced], partitionCount: 10000))
      .Throws<MixedStreamEventException>();

    await Assert.That(thrown!.StreamId).IsEqualTo(streamId);
    await Assert.That(thrown.EventId).IsEqualTo(sourced.MessageId);
    await Assert.That(thrown.Message).StartsWith(MixedStreamEventException.DIAGNOSTIC_ID);
    await Assert.That(thrown.InnerException).IsTypeOf<PostgresException>()
      .Because("the store's own error is kept for the stack trace and the SQL state");
  }

  [Test]
  public async Task StoreOutboxMessagesAsync_BackgroundWriteOfASourcedEventOnAnEphemeralStream_DeadLettersTheRowAsync() {
    var coordinator = _build();
    var streamId = (Guid)TrackedGuid.New();
    await coordinator.StoreOutboxMessagesAsync([_event(streamId, EPHEMERAL_TYPE, EventFlags.Ephemeral)], partitionCount: 10000);
    var sourced = _event(streamId, SOURCED_TYPE, EventFlags.None);

    await coordinator.StoreOutboxMessagesAsync([sourced], partitionCount: 10000);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var reason = await conn.ExecuteScalarAsync<int?>(
      "SELECT failure_reason FROM wh_dead_letters WHERE source_id = @id", new { id = sourced.MessageId });
    await Assert.That(reason).IsEqualTo((int)MessageFailureReason.MixedStreamRefused);
    var stored = await conn.ExecuteScalarAsync<long>(
      "SELECT count(*) FROM wh_event_store WHERE event_id = @id", new { id = sourced.MessageId });
    await Assert.That(stored).IsEqualTo(0L);
  }

  [Test]
  public async Task AppendAsync_SourcedEventOnAnEphemeralStream_ThrowsAndStoresNothingAsync() {
    var store = _eventStore();
    var streamId = (Guid)TrackedGuid.New();
    await store.AppendAsync(streamId, new DapperMixedStreamProbeTyping { StreamId = streamId });
    var sourced = new DapperMixedStreamProbeNoted { StreamId = streamId };

    var thrown = await Assert.That(async () => await store.AppendAsync(streamId, sourced))
      .Throws<MixedStreamEventException>();

    await Assert.That(thrown!.StreamId).IsEqualTo(streamId);
    await Assert.That(thrown.Message).Contains("ephemeral events");
    await Assert.That(await _storedCountAsync(streamId)).IsEqualTo(1L);
  }

  [Test]
  public async Task AppendAsync_EphemeralEventOnASourcedStream_ThrowsAndStoresNothingAsync() {
    var store = _eventStore();
    var streamId = (Guid)TrackedGuid.New();
    await store.AppendAsync(streamId, new DapperMixedStreamProbeNoted { StreamId = streamId });

    var thrown = await Assert.That(async () =>
        await store.AppendAsync(streamId, new DapperMixedStreamProbeTyping { StreamId = streamId }))
      .Throws<MixedStreamEventException>();

    await Assert.That(thrown!.Message).Contains("Sourced events");
    await Assert.That(await _storedCountAsync(streamId)).IsEqualTo(1L);
  }

  [Test]
  public async Task AppendAsync_EventTheResolverDeclaresEphemeral_IsStoredAsEphemeralAsync() {
    var store = _eventStore(new DeclaresEphemeral(typeof(DapperMixedStreamProbeNoted)));
    var streamId = (Guid)TrackedGuid.New();

    await store.AppendAsync(streamId, new DapperMixedStreamProbeNoted { StreamId = streamId });

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var flags = await conn.ExecuteScalarAsync<int>("SELECT flags FROM wh_event_store WHERE stream_id = @streamId", new { streamId });
    await Assert.That(flags).IsEqualTo((int)EventFlags.Ephemeral)
      .Because("[Ephemeral] on a type is resolved through the ephemeral-mode resolver, as on the dispatch path");
  }

  private DapperPostgresEventStore _eventStore(IEphemeralModeResolver? resolver = null) {
    var jsonOptions = Whizbang.Data.Dapper.Postgres.Tests.Generated.WhizbangJsonContext.CreateOptions();
    return new DapperPostgresEventStore(
      ConnectionFactory,
      Executor,
      jsonOptions,
      new EventEnvelopeJsonbAdapter(jsonOptions),
      new JsonbSizeValidator(NullLogger<JsonbSizeValidator>.Instance),
      new PolicyEngine(),
      null,
      NullLogger<DapperPostgresEventStore>.Instance,
      resolver);
  }

  private async Task<long> _storedCountAsync(Guid streamId) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_event_store WHERE stream_id = @streamId", new { streamId });
  }

  /// <summary>An ephemeral-mode resolver that declares exactly one type ephemeral, as the generated catalog would.</summary>
  private sealed class DeclaresEphemeral(Type ephemeralType) : IEphemeralModeResolver {
    public Whizbang.Core.Attributes.EphemeralInfo? Resolve(string clrTypeName) => null;
    public Whizbang.Core.Attributes.EphemeralInfo? Resolve(Type type) => null;
    public bool IsEphemeral(string clrTypeName) => false;
    public bool IsEphemeral(Type type) => type == ephemeralType;
  }

  private DapperWorkCoordinator _build() =>
    new(ConnectionString, _jsonOptions, NullLogger<DapperWorkCoordinator>.Instance);

  private static OutboxMessage _event(Guid streamId, string messageType, EventFlags flags) {
    var msgId = (Guid)TrackedGuid.New();
    var envelope = new MessageEnvelope<JsonElement>(
      MessageId.From(msgId),
      JsonElement.Parse("{\"k\":1}"),
      []);
    return new OutboxMessage {
      MessageId = msgId,
      Destination = "mixed-stream-topic",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
      MessageType = messageType,
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(msgId), Hops = [] },
      StreamId = streamId,
      IsEvent = true,
      Flags = flags,
    };
  }
}

/// <summary>An ephemeral event a direct append stores (the shipped marker declares it).</summary>
public sealed record DapperMixedStreamProbeTyping : IEphemeralEvent {
  /// <summary>The stream.</summary>
  [StreamId]
  public required Guid StreamId { get; init; }
}

/// <summary>A Sourced event a direct append stores.</summary>
public sealed record DapperMixedStreamProbeNoted : IEvent {
  /// <summary>The stream.</summary>
  [StreamId]
  public required Guid StreamId { get; init; }
}
