using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tags;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for the coalesce fold coordinator seam (increment C of tag-bound
/// coalescing): per-group pending stats, the SKIP LOCKED fetch, the one-transaction fold
/// completion, the matured release — and the end-to-end path where the real
/// <see cref="CoalesceShipWorker"/> folds singles minted through the real store seam into ONE
/// composite row while completing the singles atomically.
/// </summary>
/// <docs>fundamentals/messages/message-tags#coalescing</docs>
[Category("Shard3")]
public class CoalesceFoldCoordinatorSqlTests : EFCoreTestBase {
  [Test]
  public async Task GetPendingCoalesceGroupStats_ReturnsPerGroupCountsAndAgesAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    var connection = await _openAsync(dbContext);
    await _seedPendingAsync(connection, "group-a", createdAgoSeconds: 100);
    await _seedPendingAsync(connection, "group-a", createdAgoSeconds: 10);
    await _seedPendingAsync(connection, "group-b", createdAgoSeconds: 50);
    // A processed row must not count.
    var processedId = await _seedPendingAsync(connection, "group-a", createdAgoSeconds: 500);
    await _execAsync(connection, $"UPDATE wh_outbox SET processed_at = NOW() WHERE message_id = '{processedId}'");

    var stats = await coordinator.GetPendingCoalesceGroupStatsAsync();

    var byGroup = stats.ToDictionary(s => s.Group);
    await Assert.That(byGroup.Count).IsEqualTo(2);
    await Assert.That(byGroup["group-a"].PendingCount).IsEqualTo(2L);
    await Assert.That(byGroup["group-b"].PendingCount).IsEqualTo(1L);
    await Assert.That(byGroup["group-a"].OldestCreatedAt).IsLessThan(byGroup["group-a"].NewestCreatedAt);
  }

  [Test]
  public async Task FetchPendingCoalesce_ReturnsOldestFirstUpToLimitAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    var connection = await _openAsync(dbContext);
    var oldest = await _seedPendingAsync(connection, "group-a", createdAgoSeconds: 300);
    var middle = await _seedPendingAsync(connection, "group-a", createdAgoSeconds: 200);
    var newest = await _seedPendingAsync(connection, "group-a", createdAgoSeconds: 100);
    await _seedPendingAsync(connection, "group-b", createdAgoSeconds: 400);  // other group excluded

    var fetched = await coordinator.FetchPendingCoalesceAsync("group-a", limit: 2);

    await Assert.That(fetched.Count).IsEqualTo(2);
    await Assert.That(fetched[0].MessageId).IsEqualTo(oldest);
    await Assert.That(fetched[1].MessageId).IsEqualTo(middle);
    await Assert.That(fetched.Select(f => f.MessageId)).DoesNotContain(newest);
    await Assert.That(fetched[0].CoalesceGroup).IsEqualTo("group-a");
    await Assert.That(fetched[0].Envelope.Payload.GetProperty("record").GetString()).IsEqualTo("data")
      .Because("the fetch returns FULL rows — the stored envelope payload rides back for raw carry");
  }

  [Test]
  public async Task CompleteCoalesceFold_InsertsCompositeAndCompletesSinglesAtomicallyAsync() {
    // End to end through the REAL pieces: singles minted through the real store seam
    // (resolver-stamped, StoreOutboxMessagesAsync), then the real CoalesceShipWorker fold
    // (driven via its internal tick seam with a FakeTimeProvider advanced past the slide),
    // then SQL-level assertions: ONE composite row with N inners, N singles completed —
    // written in one transaction by CompleteCoalesceFoldAsync.
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    var connection = await _openAsync(dbContext);

    var tagOptions = new TagOptions();
    tagOptions.Coalesce("record-digest", c => {
      c.SlideSeconds = 15;
      c.MaxDelaySeconds = 120;
    });
    var mintTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
    var resolver = new CoalesceGroupResolver(tagOptions, mintTime,
      () => [_registration(typeof(CoalesceFoldProbeEvent), "record-digest")]);

    // Real mint path: resolver stamps group + floor; the real store seam persists.
    var singles = Enumerable.Range(0, 3)
      .Select(_ => resolver.ApplyCoalescePolicy(_outboxMessage(typeof(CoalesceFoldProbeEvent).AssemblyQualifiedName!)))
      .ToArray();
    await coordinator.StoreOutboxMessagesAsync(singles, partitionCount: 10000);
    foreach (var single in singles) {
      await Assert.That(single.CoalesceGroup).IsEqualTo("record-digest");
    }

    // The worker's clock sits 30s ahead: the group reads as quiet (newest ≥ SlideSeconds old).
    // The worker runs its first tick immediately after startup recovery, so StartAsync drives
    // the fold; the signaling wrapper's TCS is the completion signal (no polling).
    var workerTime = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(30));
    var signaling = new SignalingCoordinator(coordinator);
    var worker = _worker(signaling, resolver, workerTime);
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await signaling.FoldCompleted.Task.WaitAsync(TimeSpan.FromSeconds(15));
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    // ONE composite row: immediately shippable, transport-only, carrying all three inners.
    await using (var read = connection.CreateCommand()) {
      read.CommandText = @"
        SELECT message_type, coalesce_group, scheduled_for, is_event, event_data::text
        FROM wh_outbox
        WHERE message_type LIKE '%CoalescedEventsComposite%'";
      await using var reader = await read.ExecuteReaderAsync();
      await Assert.That(await reader.ReadAsync()).IsTrue().Because("the fold must insert the composite row");
      await Assert.That(reader.IsDBNull(1)).IsTrue();
      await Assert.That(reader.IsDBNull(2)).IsTrue();
      await Assert.That(reader.GetBoolean(3)).IsFalse();
      var envelope = JsonDocument.Parse(reader.GetString(4));
      var innerIds = envelope.RootElement.GetProperty("p").GetProperty("InnerEventIds")
        .EnumerateArray().Select(e => e.GetGuid()).ToList();
      await Assert.That(innerIds).IsEquivalentTo(singles.Select(s => s.MessageId).ToList());
      await Assert.That(await reader.ReadAsync()).IsFalse().Because("exactly ONE composite for one fold");
    }

    // All three singles completed.
    await using (var count = connection.CreateCommand()) {
      count.CommandText = @"
        SELECT COUNT(*) FROM wh_outbox
        WHERE coalesce_group = 'record-digest' AND processed_at IS NULL";
      var pending = (long)(await count.ExecuteScalarAsync())!;
      await Assert.That(pending).IsEqualTo(0L);
    }
  }

  [Test]
  public async Task ReleaseMaturedCoalesce_ReleasesOnlyMaturedRows_AndClaimShipsThemAsync() {
    // The shipper-death simulation: singles sit past their deadline, a recovering worker (or
    // the per-tick backstop) releases them, and the normal claim pump ships them individually.
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    var connection = await _openAsync(dbContext);
    var matured = await _seedPendingAsync(connection, "group-a", createdAgoSeconds: 300, scheduledForSql: "NOW() - INTERVAL '1 second'");
    var young = await _seedPendingAsync(connection, "group-a", createdAgoSeconds: 10, scheduledForSql: "NOW() + INTERVAL '110 seconds'");

    var released = await coordinator.ReleaseMaturedCoalesceAsync("group-a");

    await Assert.That(released).IsEqualTo(1);

    await using (var read = connection.CreateCommand()) {
      read.CommandText = "SELECT coalesce_group IS NULL AND scheduled_for IS NULL FROM wh_outbox WHERE message_id = @id";
      read.Parameters.AddWithValue("id", matured);
      await Assert.That((bool)(await read.ExecuteScalarAsync())!).IsTrue();
    }
    await using (var read = connection.CreateCommand()) {
      read.CommandText = "SELECT coalesce_group FROM wh_outbox WHERE message_id = @id";
      read.Parameters.AddWithValue("id", young);
      await Assert.That((string?)await read.ExecuteScalarAsync()).IsEqualTo("group-a")
        .Because("a row whose floor has not matured stays pending for the fold");
    }

    // The released row ships through the normal pump.
    var instanceId = Guid.NewGuid();
    await _execAsync(connection, $@"
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES ('{instanceId}', 'test', 'test-host', 1, NOW(), NOW(), '{{}}'::jsonb)");
    await using (var claim = connection.CreateCommand()) {
      claim.CommandText = @"
        SELECT work_id FROM claim_work(
          p_instance_id => @id, p_service_name => 'test', p_host_name => 'test-host',
          p_process_id => 1, p_max_streams => 100, p_partition_count => 10000, p_lease_seconds => 300)
        WHERE source = 'outbox'";
      claim.Parameters.AddWithValue("id", instanceId);
      var claimed = new List<Guid>();
      await using var reader = await claim.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        claimed.Add(reader.GetGuid(0));
      }
      await Assert.That(claimed).Contains(matured);
      await Assert.That(claimed).DoesNotContain(young);
    }
  }

  #region Helpers

  private static IWorkCoordinator _coordinator(WorkCoordinationDbContext dbContext) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private static CoalesceShipWorker _worker(
      IWorkCoordinator coordinator,
      CoalesceGroupResolver resolver,
      FakeTimeProvider time) {
    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton(new WorkCoordinatorOptions());
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new CoalesceShipWorker(
      sp.GetRequiredService<IServiceScopeFactory>(), gate, resolver, logger: null, timeProvider: time);
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static async Task _execAsync(NpgsqlConnection connection, string sql) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<Guid> _seedPendingAsync(
      NpgsqlConnection connection,
      string group,
      int createdAgoSeconds,
      string? scheduledForSql = null) {
    var messageId = (Guid)TrackedGuid.NewMedo();
    await using var ins = connection.CreateCommand();
    ins.CommandText = $@"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts,
         created_at, stream_id, partition_number, coalesce_group, scheduled_for)
      VALUES (@msg, 'test-topic', 'TestEvent',
        '{{""id"":""{messageId}"",""p"":{{""record"":""data""}},""h"":[]}}',
        '{{}}', 0, 0,
        NOW() - INTERVAL '{createdAgoSeconds} seconds', @stream, 0, @grp,
        {(scheduledForSql ?? "NOW() + INTERVAL '60 seconds'")})";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("stream", Guid.NewGuid());
    ins.Parameters.AddWithValue("grp", group);
    await ins.ExecuteNonQueryAsync();
    return messageId;
  }

  /// <summary>
  /// Forwards the coalesce seam to the real coordinator and signals when a fold completes —
  /// the deterministic completion signal for driving the hosted worker end to end.
  /// </summary>
  private sealed class SignalingCoordinator(IWorkCoordinator inner) : IWorkCoordinator {
    public TaskCompletionSource FoldCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<IReadOnlyList<CoalesceGroupStats>> GetPendingCoalesceGroupStatsAsync(CancellationToken cancellationToken = default)
      => inner.GetPendingCoalesceGroupStatsAsync(cancellationToken);

    public Task<IReadOnlyList<OutboxMessage>> FetchPendingCoalesceAsync(string group, int limit, CancellationToken cancellationToken = default)
      => inner.FetchPendingCoalesceAsync(group, limit, cancellationToken);

    public async Task CompleteCoalesceFoldAsync(IReadOnlyList<Guid> foldedIds, OutboxMessage[] compositeMessages, int partitionCount, CancellationToken cancellationToken = default) {
      await inner.CompleteCoalesceFoldAsync(foldedIds, compositeMessages, partitionCount, cancellationToken);
      FoldCompleted.TrySetResult();
    }

    public Task<int> ReleaseMaturedCoalesceAsync(string group, CancellationToken cancellationToken = default)
      => inner.ReleaseMaturedCoalesceAsync(group, cancellationToken);

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default)
      => inner.DeregisterInstanceAsync(instanceId, cancellationToken);

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
      => inner.GatherStatisticsAsync(cancellationToken);

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default)
      => inner.StoreInboxMessagesAsync(messages, partitionCount, cancellationToken);

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken ct = default)
      => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken ct = default)
      => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken ct = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private static Whizbang.Core.Tags.MessageTagRegistration _registration(Type messageType, string tag) => new() {
    MessageType = messageType,
    AttributeType = typeof(Whizbang.Core.Attributes.SignalTagAttribute),
    Tag = tag,
    PayloadBuilder = _ => JsonSerializer.SerializeToElement(new { }),
    AttributeFactory = () => new Whizbang.Core.Attributes.SignalTagAttribute { Tag = tag }
  };

  private static OutboxMessage _outboxMessage(string messageType) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonSerializer.SerializeToElement(new { record = "data" }),
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox }
    };
    return new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = "test-topic",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [] },
      EnvelopeType = "TestEnvelopeType",
      StreamId = Guid.NewGuid(),
      IsEvent = false,
      MessageType = messageType
    };
  }

  #endregion
}

/// <summary>Public probe type: stands in for a coalesce-bound application message type.</summary>
public sealed record CoalesceFoldProbeEvent(Guid Id);
