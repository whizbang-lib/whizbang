// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>An ephemeral event a direct append stores (the shipped marker declares it).</summary>
public sealed record MixedStreamProbeTyping : IEphemeralEvent {
  /// <summary>The conversation being typed in, which is the stream.</summary>
  [StreamId]
  public Guid ConversationId { get; init; }
}

/// <summary>A Sourced event a direct append stores.</summary>
public sealed record MixedStreamProbeNoted : IEvent {
  /// <summary>The order noted, which is the stream.</summary>
  [StreamId]
  public Guid OrderId { get; init; }
}

/// <summary>An event that is ephemeral only by what the ephemeral-mode resolver says about its type.</summary>
public sealed record MixedStreamProbeDeclared : IEvent {
  /// <summary>The subject, which is the stream.</summary>
  [StreamId]
  public Guid SubjectId { get; init; }
}

/// <summary>
/// WHIZ133 (#1546) on the direct append, <c>IEventStore.AppendAsync</c> through <c>wh_append_event</c>: it stores
/// without the outbox or the inbox emit chain, so it refuses for itself. The caller awaits it, so a refusal fails the
/// append with <see cref="MixedStreamEventException"/> and stores nothing. The event's kind is stored with it, so the
/// guard on every path reads the same thing; a compaction origin is neither kind and is never refused.
/// </summary>
/// <docs>operations/diagnostics/whiz133</docs>
[Category("Shard5")]
public class DirectAppendMixedStreamGuardTests : EFCoreTestBase {
  private const string EPHEMERAL_OUTBOX_TYPE = "Probe.Contracts.UserIsTyping, Probe.Contracts";

  [Test]
  public async Task DirectAppend_SourcedEventOnAnEphemeralStream_ThrowsAndStoresNothingAsync() {
    var streamId = (Guid)TrackedGuid.New();
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    await store.AppendAsync(streamId, _envelope(new MixedStreamProbeTyping { ConversationId = streamId }));
    var sourced = _envelope(new MixedStreamProbeNoted { OrderId = streamId });

    var thrown = await Assert.That(async () => await store.AppendAsync(streamId, sourced))
      .Throws<MixedStreamEventException>();

    await Assert.That(thrown!.StreamId).IsEqualTo(streamId);
    await Assert.That(thrown.EventId).IsEqualTo(sourced.MessageId.Value);
    await Assert.That(thrown.Message).Contains("ephemeral events");
    await Assert.That(await _storedCountAsync(context, streamId)).IsEqualTo(1L)
      .Because("a refused append stores nothing");
  }

  [Test]
  public async Task DirectAppend_EphemeralEventOnASourcedStream_ThrowsAsync() {
    var streamId = (Guid)TrackedGuid.New();
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    await store.AppendAsync(streamId, _envelope(new MixedStreamProbeNoted { OrderId = streamId }));

    var thrown = await Assert.That(async () =>
        await store.AppendAsync(streamId, _envelope(new MixedStreamProbeTyping { ConversationId = streamId })))
      .Throws<MixedStreamEventException>();

    await Assert.That(thrown!.Message).Contains("Sourced events");
    await Assert.That(await _storedCountAsync(context, streamId)).IsEqualTo(1L);
  }

  [Test]
  public async Task DirectAppend_SourcedEventOnAStreamTheOutboxMadeEphemeral_ThrowsAsync() {
    var streamId = (Guid)TrackedGuid.New();
    await using var context = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(context, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.StoreOutboxMessagesAsync([_ephemeralOutbox(streamId)], partitionCount: 10000);
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    await Assert.That(async () =>
        await store.AppendAsync(streamId, _envelope(new MixedStreamProbeNoted { OrderId = streamId })))
      .Throws<MixedStreamEventException>();
  }

  [Test]
  public async Task DirectAppend_EphemeralEvent_IsStoredAsEphemeralAsync() {
    var streamId = (Guid)TrackedGuid.New();
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    await store.AppendAsync(streamId, _envelope(new MixedStreamProbeTyping { ConversationId = streamId }));

    await Assert.That(await _flagsAsync(context, streamId)).IsEquivalentTo([(int)EventFlags.Ephemeral])
      .Because("the row states its kind, as an outbox or inbox copy of the same event does");
  }

  [Test]
  public async Task DirectAppend_EventTheResolverDeclaresEphemeral_IsRefusedOnASourcedStreamAsync() {
    var streamId = (Guid)TrackedGuid.New();
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(
      context, ephemeralModeResolver: new DeclaresEphemeral(typeof(MixedStreamProbeDeclared)));
    await store.AppendAsync(streamId, _envelope(new MixedStreamProbeNoted { OrderId = streamId }));

    await Assert.That(async () =>
        await store.AppendAsync(streamId, _envelope(new MixedStreamProbeDeclared { SubjectId = streamId })))
      .Throws<MixedStreamEventException>()
      .Because("[Ephemeral] on a type is resolved through the ephemeral-mode resolver, as on the dispatch path");
  }

  [Test]
  public async Task DirectAppend_CompactionOriginOnAnEphemeralStream_IsStored_AndTheStreamStaysEphemeralAsync() {
    var streamId = (Guid)TrackedGuid.New();
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    await store.AppendAsync(streamId, _envelope(new MixedStreamProbeTyping { ConversationId = streamId }));

    // What StreamCompactor appends at the head of an ephemeral stream before it truncates the folded detail.
    await store.AppendAsync(streamId, _envelope(new Compacted {
      StreamId = streamId,
      PerspectiveName = "Probe.TypingPerspective",
      Model = JsonSerializer.SerializeToElement(new { typing = true }),
      ThroughVersion = 0,
    }));
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(context, JsonContextRegistry.CreateCombinedOptions());
    var later = _ephemeralOutbox(streamId);
    await coordinator.StoreOutboxMessagesAsync([later], partitionCount: 10000);

    await Assert.That(await _flagsAsync(context, streamId))
      .IsEquivalentTo([(int)EventFlags.Ephemeral, (int)EventFlags.Compacted, (int)EventFlags.Ephemeral])
      .Because("a compaction origin is neither kind: it is stored on an ephemeral stream, and the stream goes on "
        + "taking ephemeral events after it");
  }

  private static MessageEnvelope<T> _envelope<T>(T payload) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UnixEpoch,
        ServiceInstance = ServiceInstanceInfo.Unknown,
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  private static OutboxMessage _ephemeralOutbox(Guid streamId) =>
    CreateTestOutboxMessage((Guid)TrackedGuid.New(), "mixed-stream-topic", streamId, isEvent: true) with {
      MessageType = EPHEMERAL_OUTBOX_TYPE,
      Flags = EventFlags.Ephemeral,
    };

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext context) {
    var conn = (NpgsqlConnection)context.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task<long> _storedCountAsync(WorkCoordinationDbContext context, Guid streamId) {
    var conn = await _openAsync(context);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_event_store WHERE stream_id = @stream";
    cmd.Parameters.AddWithValue("stream", streamId);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<List<int>> _flagsAsync(WorkCoordinationDbContext context, Guid streamId) {
    var conn = await _openAsync(context);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT flags FROM wh_event_store WHERE stream_id = @stream ORDER BY version";
    cmd.Parameters.AddWithValue("stream", streamId);
    var flags = new List<int>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      flags.Add(reader.GetInt32(0));
    }
    return flags;
  }

  /// <summary>An ephemeral-mode resolver that declares exactly one type ephemeral, as the generated catalog would.</summary>
  private sealed class DeclaresEphemeral(Type ephemeralType) : IEphemeralModeResolver {
    public EphemeralInfo? Resolve(string clrTypeName) => null;
    public EphemeralInfo? Resolve(Type type) => null;
    public bool IsEphemeral(string clrTypeName) => false;
    public bool IsEphemeral(Type type) => type == ephemeralType;
  }
}
