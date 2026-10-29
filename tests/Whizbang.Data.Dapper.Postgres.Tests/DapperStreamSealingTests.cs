// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Data;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Custom;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>A terminal event the Dapper store appends: storing one ends its stream.</summary>
[TerminalEvent]
public sealed record DapperSealingProbeClosed : IEvent {
  /// <summary>The stream.</summary>
  [StreamId]
  public required Guid StreamId { get; init; }
}

/// <summary>
/// Stream sealing (#1733, WS-3 S4) on the Dapper driver: its event store refuses an append to a sealed stream with
/// <see cref="StreamSealedException"/>, a terminal event it appends seals the stream, and the driver registers the
/// finalizer. The SQL itself is covered by the EF Core <c>StreamSealingSqlTests</c>; these lock this driver's side.
/// </summary>
/// <docs>fundamentals/events/stream-end-of-life#sealing-a-stream</docs>
public class DapperStreamSealingTests : PostgresTestBase {
  private const int STREAM_SEALED_REASON = 25;
  private static readonly DateTimeOffset _t0 = new(2026, 10, 29, 12, 10, 0, TimeSpan.Zero);

  [Test]
  public async Task AppendAsync_TerminalEvent_SealsTheStream_AndTheNextAppendThrowsStreamSealedExceptionAsync() {
    var store = _eventStore();
    var stream = (Guid)TrackedGuid.New();
    await store.AppendAsync(stream, new DapperMixedStreamProbeNoted { StreamId = stream });

    await store.AppendAsync(stream, new DapperSealingProbeClosed { StreamId = stream });

    await using var conn = await _openAsync();
    await Assert.That(await conn.ExecuteScalarAsync<string>("SELECT state FROM wh_stream_lifecycle WHERE stream_id = @stream", new { stream }))
      .IsEqualTo("Sealed").Because("the Dapper store stamps [TerminalEvent] as EventFlags.Terminal, and wh_append_event seals on it");
    var thrown = await Assert.That(async () => await store.AppendAsync(stream, new DapperMixedStreamProbeNoted { StreamId = stream }))
      .Throws<StreamSealedException>();
    await Assert.That(thrown!.StreamId).IsEqualTo(stream);
    await Assert.That(thrown.Reason).StartsWith($"Terminal event {typeof(DapperSealingProbeClosed).FullName} (event ");
    await Assert.That(thrown.InnerException).IsTypeOf<PostgresException>()
      .Because("the store's own error is kept for the stack trace and the SQL state");
    await Assert.That(await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_event_store WHERE stream_id = @stream", new { stream }))
      .IsEqualTo(2L);
  }

  [Test]
  public async Task Finalize_ThroughTheRegisteredFinalizer_SealsTheStream_AndTheNextAppendThrowsAsync() {
    var withSql = new ServiceCollection().AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), false, (string?)null, null);
    var withEntries = new ServiceCollection().AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), false, [], null);
    var store = _eventStore();

    foreach (var services in new[] { withSql, withEntries }) {
      services.AddSingleton<TimeProvider>(new FixedClock(_t0));
      await using var provider = services.BuildServiceProvider();
      var finalizer = provider.GetRequiredService<IStreamFinalizer>();
      await Assert.That(finalizer).IsTypeOf<PostgresStreamFinalizer>();
      var stream = (Guid)TrackedGuid.New();
      await store.AppendAsync(stream, new DapperMixedStreamProbeNoted { StreamId = stream });

      var sealedResult = await finalizer.FinalizeAsync(stream, "Closed by the owner");
      var again = await finalizer.FinalizeAsync(stream, "Closed again");

      await Assert.That(sealedResult).IsEqualTo(new StreamFinalizeResult(stream, StreamFinalizeStatus.Sealed, _t0, "Closed by the owner"))
        .Because("the seal time is the registered clock's");
      await Assert.That(again).IsEqualTo(new StreamFinalizeResult(stream, StreamFinalizeStatus.AlreadySealed, _t0, "Closed by the owner"));
      var thrown = await Assert.That(async () => await store.AppendAsync(stream, new DapperMixedStreamProbeNoted { StreamId = stream }))
        .Throws<StreamSealedException>();
      await Assert.That(thrown!.SealedAt).IsEqualTo(_t0);
      await Assert.That(thrown.Reason).IsEqualTo("Closed by the owner");
    }
  }

  [Test]
  public async Task StoreOutboxMessagesAsync_ToASealedStream_DeadLettersTheEvent_AndStoresTheRestAsync() {
    var coordinator = new DapperWorkCoordinator(
      ConnectionString, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(), NullLogger<DapperWorkCoordinator>.Instance);
    var stream = (Guid)TrackedGuid.New();
    var other = (Guid)TrackedGuid.New();
    await coordinator.StoreOutboxMessagesAsync([_outbox(stream)], partitionCount: 10000);
    await new PostgresStreamFinalizer(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync).FinalizeAsync(stream, "Closed");
    var refused = _outbox(stream);
    var stored = _outbox(other);

    await coordinator.StoreOutboxMessagesAsync([refused, stored], partitionCount: 10000);

    await using var conn = await _openAsync();
    await Assert.That(await conn.ExecuteScalarAsync<int?>(
      "SELECT failure_reason FROM wh_dead_letters WHERE source_id = @id", new { id = refused.MessageId })).IsEqualTo(STREAM_SEALED_REASON);
    await Assert.That(await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_event_store WHERE stream_id = @other", new { other }))
      .IsEqualTo(1L).Because("the refusal is the sealed stream's event alone; the other stream's event is stored");
  }

  // ===== Helpers =====

  private sealed class FixedClock(DateTimeOffset now) : TimeProvider {
    public override DateTimeOffset GetUtcNow() => now;
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private DapperPostgresEventStore _eventStore() {
    var jsonOptions = Whizbang.Data.Dapper.Postgres.Tests.Generated.WhizbangJsonContext.CreateOptions();
    return new DapperPostgresEventStore(
      ConnectionFactory,
      Executor,
      jsonOptions,
      new EventEnvelopeJsonbAdapter(jsonOptions),
      new JsonbSizeValidator(NullLogger<JsonbSizeValidator>.Instance),
      new PolicyEngine(),
      null,
      NullLogger<DapperPostgresEventStore>.Instance);
  }

  private static OutboxMessage _outbox(Guid streamId) {
    var msgId = (Guid)TrackedGuid.New();
    return new OutboxMessage {
      MessageId = msgId,
      Destination = "sealing-topic",
      Envelope = new MessageEnvelope<JsonElement>(MessageId.From(msgId), JsonElement.Parse("{\"k\":1}"), []),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
      MessageType = "Probe.Contracts.Deposited, Probe.Contracts",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(msgId), Hops = [] },
      StreamId = streamId,
      IsEvent = true,
    };
  }
}
