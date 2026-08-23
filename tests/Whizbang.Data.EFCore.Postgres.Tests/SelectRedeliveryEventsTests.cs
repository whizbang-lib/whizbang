using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Stream-integrity R1a: <c>SelectRedeliveryEventsAsync</c> selects persisted events for
/// re-delivery in original stored form, ordered (stream, version), with the two built-in
/// exclusions — at-most-once schedule occurrences (delivery guarantee forbids re-delivery) and
/// reaped ephemeral events (absent body = structurally unrepairable, accepted loss).
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/Redelivery.cs</code-under-test>
[Category("Integration")]
[NotInParallel("RedeliverySelection")]
[Category("Shard4")]
public class SelectRedeliveryEventsTests : EFCoreTestBase {

  private const string TENANT_A = "tenant-a";
  private const string TENANT_B = "tenant-b";

  // Typed as the interface: the default-interface implementation (and later the EFCore override)
  // are both reached the way production callers reach them.
  private static IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task Select_OrdersFiltersAndExcludes_ByContractAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var coordinator = _coordinator(ctx);

    var stream1 = TrackedGuid.NewMedo().Value;
    var stream2 = TrackedGuid.NewMedo().Value;
    var streamOtherTenant = TrackedGuid.NewMedo().Value;

    // Stream 1, tenant A — versions inserted OUT OF ORDER to prove result ordering.
    var e1V2 = await _seedAsync(conn, stream1, version: 2, TENANT_A, "Contracts.ThingUpdated");
    var e1V1 = await _seedAsync(conn, stream1, version: 1, TENANT_A, "Contracts.ThingCreated");
    // At-most-once occurrence on stream 1 → excluded by the selection itself.
    var atMostOnce = await _seedAsync(conn, stream1, version: 3, TENANT_A, "Contracts.OccurrenceFired",
      metadataJson: "{\"scheduleId\":\"s-1\",\"deliveryGuarantee\":1}");
    // At-least-once occurrence → included (discriminates the guarantee filter from "any occurrence").
    var atLeastOnce = await _seedAsync(conn, stream1, version: 4, TENANT_A, "Contracts.OccurrenceFired",
      metadataJson: "{\"scheduleId\":\"s-2\",\"deliveryGuarantee\":0}");

    // Stream 2, tenant A — ephemeral pair: body present (in grace) vs body reaped.
    var ephemeralInGrace = await _seedAsync(conn, stream2, version: 1, TENANT_A, "Contracts.PresencePing", flags: 8);
    var ephemeralReaped = await _seedAsync(conn, stream2, version: 2, TENANT_A, "Contracts.PresencePing",
      flags: 8, reapBody: true);

    // Tenant B — excluded by the tenant filter.
    await _seedAsync(conn, streamOtherTenant, version: 1, TENANT_B, "Contracts.ThingCreated");

    var selected = await coordinator.SelectRedeliveryEventsAsync(
      new RedeliveryRequest { TenantScope = TENANT_A });

    var ids = selected.Select(e => e.EventId).ToList();
    await Assert.That(ids).Contains(e1V1);
    await Assert.That(ids).Contains(e1V2);
    await Assert.That(ids).Contains(atLeastOnce);
    await Assert.That(ids).Contains(ephemeralInGrace)
      .Because("an ephemeral event still holding its body (inside grace) is repairable.");
    await Assert.That(ids.Contains(atMostOnce)).IsFalse()
      .Because("an at-most-once occurrence must never be selected for re-delivery — that is the " +
               "exact guarantee it was declared for.");
    await Assert.That(ids.Contains(ephemeralReaped)).IsFalse()
      .Because("a reaped ephemeral body is structurally unrepairable — accepted ephemeral loss.");
    await Assert.That(selected.Count).IsEqualTo(4)
      .Because("tenant B's event is outside the requested tenant scope.");

    // Ordering: (stream, version) — stream 1 versions ascend despite out-of-order insertion.
    var stream1Rows = selected.Where(e => e.StreamId == stream1).ToList();
    await Assert.That(stream1Rows.Select(e => e.Version).ToList()).IsEquivalentTo([1L, 2L, 4L])
      .Because("per-stream results replay in append order regardless of physical insert order.");
    await Assert.That(stream1Rows[0].EventId).IsEqualTo(e1V1);

    // Original stored form comes back.
    await Assert.That(stream1Rows[0].EventType).IsEqualTo("Contracts.ThingCreated");
    await Assert.That(stream1Rows[0].EventData).Contains("\"seeded\"");
    await Assert.That(stream1Rows[0].Scope!).Contains(TENANT_A);
  }

  [Test]
  public async Task Select_HonorsStreamTypeSequenceFiltersAndCapAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var coordinator = _coordinator(ctx);

    var streamA = TrackedGuid.NewMedo().Value;
    var streamB = TrackedGuid.NewMedo().Value;
    await _seedAsync(conn, streamA, version: 1, TENANT_A, "Contracts.TypeOne");
    await _seedAsync(conn, streamA, version: 2, TENANT_A, "Contracts.TypeTwo");
    await _seedAsync(conn, streamB, version: 1, TENANT_A, "Contracts.TypeOne");

    var byStream = await coordinator.SelectRedeliveryEventsAsync(
      new RedeliveryRequest { TenantScope = TENANT_A, StreamIds = [streamA] });
    await Assert.That(byStream.All(e => e.StreamId == streamA)).IsTrue();
    await Assert.That(byStream.Count).IsEqualTo(2);

    var byType = await coordinator.SelectRedeliveryEventsAsync(
      new RedeliveryRequest { TenantScope = TENANT_A, EventTypes = ["Contracts.TypeOne"], StreamIds = [streamA, streamB] });
    await Assert.That(byType.All(e => e.EventType == "Contracts.TypeOne")).IsTrue();
    await Assert.That(byType.Count).IsEqualTo(2);

    var capped = await coordinator.SelectRedeliveryEventsAsync(
      new RedeliveryRequest { TenantScope = TENANT_A, StreamIds = [streamA, streamB], MaxEvents = 1 });
    await Assert.That(capped.Count).IsEqualTo(1)
      .Because("MaxEvents is the storm-cap building block — a hard LIMIT, never advisory.");

    var all = await coordinator.SelectRedeliveryEventsAsync(
      new RedeliveryRequest { TenantScope = TENANT_A, StreamIds = [streamA, streamB] });
    var maxSeq = all.Max(e => e.CommitSequence!.Value);
    var below = await coordinator.SelectRedeliveryEventsAsync(
      new RedeliveryRequest { TenantScope = TENANT_A, StreamIds = [streamA, streamB], ToCommitSequence = maxSeq - 1 });
    await Assert.That(below.Count).IsEqualTo(all.Count - 1)
      .Because("ToCommitSequence is the inclusive comparison watermark.");
    var above = await coordinator.SelectRedeliveryEventsAsync(
      new RedeliveryRequest { TenantScope = TENANT_A, StreamIds = [streamA, streamB], FromCommitSequence = maxSeq - 1 });
    await Assert.That(above.Count).IsEqualTo(1)
      .Because("FromCommitSequence is the exclusive floor.");
  }

  [Test]
  public async Task Select_KeysetContinuation_PagesWithoutOverlapOrLossAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var coordinator = _coordinator(ctx);

    var streamA = TrackedGuid.NewMedo().Value;
    var streamB = TrackedGuid.NewMedo().Value;
    var streams = new List<Guid> { streamA, streamB };
    await _seedAsync(conn, streamA, version: 1, TENANT_A, "Contracts.PageProbe");
    await _seedAsync(conn, streamA, version: 2, TENANT_A, "Contracts.PageProbe");
    await _seedAsync(conn, streamA, version: 3, TENANT_A, "Contracts.PageProbe");
    await _seedAsync(conn, streamB, version: 1, TENANT_A, "Contracts.PageProbe");
    await _seedAsync(conn, streamB, version: 2, TENANT_A, "Contracts.PageProbe");

    // Page through with MaxEvents=2, continuing strictly after each page's last (stream, version).
    var seen = new List<(Guid Stream, long Version)>();
    Guid? afterStream = null;
    long? afterVersion = null;
    for (var page = 0; page < 4; page++) {
      var rows = await coordinator.SelectRedeliveryEventsAsync(new RedeliveryRequest {
        TenantScope = TENANT_A,
        StreamIds = streams,
        MaxEvents = 2,
        AfterStreamId = afterStream,
        AfterVersion = afterVersion
      });
      if (rows.Count == 0) {
        break;
      }
      seen.AddRange(rows.Select(r => (r.StreamId, r.Version)));
      afterStream = rows[^1].StreamId;
      afterVersion = rows[^1].Version;
    }

    await Assert.That(seen.Count).IsEqualTo(5)
      .Because("keyset pages must cover the full selection with no loss and no overlap — the " +
               "origin's memory bound depends on paging being exact.");
    await Assert.That(seen.Distinct().Count()).IsEqualTo(5);
    // Per-stream version order must survive page boundaries (bundle ordering key). Stream-to-
    // stream order is the database's uuid collation — deliberately not asserted from C#.
    foreach (var stream in streams) {
      var versions = seen.Where(s => s.Stream == stream).Select(s => s.Version).ToList();
      await Assert.That(versions).IsEquivalentTo(versions.OrderBy(v => v).ToList())
        .Because("a stream's versions arrive ascending across pages — repair bundles replay in order.");
    }
  }

  // ── seeding ──────────────────────────────────────────────────────────────

  /// <summary>Seeds one event: store pointer + (unless <paramref name="reapBody"/>) its body row.</summary>
  private static async Task<Guid> _seedAsync(
      NpgsqlConnection conn, Guid streamId, int version, string tenant, string eventType,
      int flags = 0, string? metadataJson = null, bool reapBody = false) {
    var eventId = TrackedGuid.NewMedo().Value;
    await using (var store = conn.CreateCommand()) {
      store.CommandText = @"
        INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, commit_sequence, flags)
        VALUES (@event, @stream, @stream, 'TestAggregate', @type, @scope::jsonb, @version, nextval('wh_commit_seq'), @flags)";
      store.Parameters.AddWithValue("event", eventId);
      store.Parameters.AddWithValue("stream", streamId);
      store.Parameters.AddWithValue("type", eventType);
      store.Parameters.AddWithValue("scope", $"{{\"t\":\"{tenant}\"}}");
      store.Parameters.AddWithValue("version", version);
      store.Parameters.AddWithValue("flags", flags);
      await store.ExecuteNonQueryAsync();
    }
    if (!reapBody) {
      await using var body = conn.CreateCommand();
      body.CommandText = @"
        INSERT INTO wh_event_body (event_id, event_data, metadata)
        VALUES (@event, '{""seeded"":true}'::jsonb, @meta::jsonb)";
      body.Parameters.AddWithValue("event", eventId);
      body.Parameters.AddWithValue("meta", (object?)metadataJson ?? "{}");
      await body.ExecuteNonQueryAsync();
    }
    return eventId;
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }
}
