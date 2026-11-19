// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>The payload type the trace store corpus carries.</summary>
public sealed record TraceStoreProbePlaced(int Number);

/// <summary>
/// The Postgres route and trace stores (#1726): the trace store answers every query the in-memory one answers on the
/// same corpus, from staged forms and from distilled traces alike; the route store returns routes with their counts,
/// whether their traces are retained or gone; and either can be registered without the other.
/// </summary>
/// <docs>fundamentals/persistence/observability#the-postgres-stores</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresRouteTraceStore.cs</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class RouteTraceStoreTests : EFCoreTestBase {
  private const string ORDER_PLACED = "MyApp.Orders.OrderPlaced, MyApp";
  private const string ORDER_SHIPPED = "MyApp.Orders.OrderShipped, MyApp";
  private static readonly DateTimeOffset _t0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
  private static readonly string _probeType = TypeNameFormatter.Format(typeof(TraceStoreProbePlaced));

  // ===== ITraceStore =====

  [Test]
  public async Task TraceStore_AnswersEveryQuery_LikeTheInMemoryStore_OnTheSameCorpusAsync() {
    await using var conn = await _openAsync();
    var c1 = CorrelationId.FromExternal(Guid.Parse("01900000-0000-7000-8000-0000000000d1"));
    var c2 = CorrelationId.FromExternal(Guid.Parse("01900000-0000-7000-8000-0000000000d2"));
    var root = _envelope(c1, null, _t0, 1);
    var child = _envelope(c1, root.MessageId, _t0.AddMinutes(1), 2);
    var grandchild = _envelope(c1, child.MessageId, _t0.AddMinutes(2), 3);
    var unrelated = _envelope(c2, null, _t0.AddMinutes(3), 4);
    var sibling = _envelope(c1, root.MessageId, _t0.AddMinutes(4), 5);
    var memory = new InMemoryTraceStore();
    foreach (var envelope in new[] { root, child, unrelated }) {
      await memory.StoreAsync(envelope);
      await _appendAsync(conn, envelope);
    }
    await _executeAsync(conn, "SELECT * FROM wh_distill_event_metadata(1000)");
    foreach (var envelope in new[] { grandchild, sibling }) {
      await memory.StoreAsync(envelope);
      await _appendAsync(conn, envelope);
    }
    var postgres = new PostgresRouteTraceStore(_database());

    await Assert.That(await _countAsync(conn, "SELECT count(*) FROM wh_traces")).IsEqualTo(3L).Because("control: three events are answered from traces");
    await Assert.That(await _countAsync(conn, "SELECT count(*) FROM wh_event_metadata_staging")).IsEqualTo(2L).Because("control: two from staged forms");

    foreach (var messageId in new[] { root, child, grandchild, unrelated, sibling }.Select(e => e.MessageId)) {
      var fromPostgres = await postgres.GetByMessageIdAsync(messageId);
      var fromMemory = await memory.GetByMessageIdAsync(messageId);
      await Assert.That(fromPostgres!.MessageId).IsEqualTo(fromMemory!.MessageId);
      await Assert.That(fromPostgres.GetCorrelationId()).IsEqualTo(fromMemory.GetCorrelationId());
      await Assert.That(fromPostgres.GetCausationId()).IsEqualTo(fromMemory.GetCausationId());
      await Assert.That(fromPostgres.Hops.Single().ServiceInstance).IsEqualTo(fromMemory.Hops.Single().ServiceInstance)
        .Because("the full hops come back, instance included");
      await Assert.That(((JsonElement)fromPostgres.Payload).GetProperty("Number").GetInt32()).IsEqualTo(((TraceStoreProbePlaced)fromMemory.Payload).Number);
      await Assert.That(await postgres.GetRouteIdAsync(messageId)).IsEqualTo(await memory.GetRouteIdAsync(messageId))
        .Because("both stores name the same route for the same event");
    }

    await Assert.That(_ids(await postgres.GetByCorrelationAsync(c1))).IsEquivalentTo(_ids(await memory.GetByCorrelationAsync(c1)), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(_ids(await postgres.GetByCorrelationAsync(c2))).IsEquivalentTo(_ids(await memory.GetByCorrelationAsync(c2)), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(_ids(await postgres.GetCausalChainAsync(child.MessageId))).IsEquivalentTo(_ids(await memory.GetCausalChainAsync(child.MessageId)), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(_ids(await postgres.GetCausalChainAsync(root.MessageId))).IsEquivalentTo(_ids(await memory.GetCausalChainAsync(root.MessageId)), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(_ids(await postgres.GetByTimeRangeAsync(_t0.AddMinutes(1), _t0.AddMinutes(3)))).IsEquivalentTo(
      _ids(await memory.GetByTimeRangeAsync(_t0.AddMinutes(1), _t0.AddMinutes(3))), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That((await postgres.GetCausalChainAsync(root.MessageId)).Count).IsEqualTo(4).Because("control: the chain reaches staged and distilled events");

    var unknown = MessageId.New();
    await Assert.That(await postgres.GetByMessageIdAsync(unknown)).IsNull();
    await Assert.That(await postgres.GetRouteIdAsync(unknown)).IsNull();
    await Assert.That(await postgres.GetCausalChainAsync(unknown)).IsEmpty();
    await Assert.That(await postgres.GetByCorrelationAsync(CorrelationId.New())).IsEmpty();
  }

  [Test]
  public async Task GetByMessageId_ReadsStaging_ThenTraceAndRoute_ThenTheRowAsync() {
    await using var conn = await _openAsync();
    var envelope = _envelope(CorrelationId.New(), null, _t0, 1);
    await _appendAsync(conn, envelope);
    var store = new PostgresRouteTraceStore(_database(schema: "public"));

    var staged = await store.GetByMessageIdAsync(envelope.MessageId);
    await _executeAsync(conn, "SELECT * FROM wh_distill_event_metadata(1000)");
    var traced = await store.GetByMessageIdAsync(envelope.MessageId);
    await _executeAsync(conn, "DELETE FROM wh_traces");
    var rowOnly = await store.GetByMessageIdAsync(envelope.MessageId);

    await Assert.That(staged!.Hops.Single().ServiceInstance).IsEqualTo(envelope.Hops[0].ServiceInstance);
    await Assert.That(traced!.Hops.Single().ServiceInstance).IsEqualTo(envelope.Hops[0].ServiceInstance)
      .Because("within the trace window the full hop is rebuilt");
    await Assert.That(rowOnly!.Hops.Single().ServiceInstance).IsEqualTo(ServiceInstanceInfo.Unknown)
      .Because("past it, the row's slim hop is what is left");
    await Assert.That(rowOnly.GetCorrelationId()).IsEqualTo(envelope.GetCorrelationId());
    await Assert.That(await store.GetRouteIdAsync(envelope.MessageId)).IsEqualTo(RouteNormalizer.RouteId(_probeType, envelope.Hops));
    await store.StoreAsync(envelope);
    await Assert.That(await _countAsync(conn, "SELECT count(*) FROM wh_event_metadata_staging")).IsEqualTo(0L).Because("StoreAsync writes nothing: the write paths fill this store");
    await Assert.That(() => store.StoreAsync(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task GetByMessageId_AnEventWhoseRowIsGone_ComesBackFromItsTrace_WithANullPayloadAsync() {
    await using var conn = await _openAsync();
    var envelope = _envelope(CorrelationId.New(), null, _t0, 1);
    await _appendAsync(conn, envelope);
    await _executeAsync(conn, "SELECT * FROM wh_distill_event_metadata(1000)");
    await _executeAsync(conn, "DELETE FROM wh_event_body WHERE event_id = @e", ("e", envelope.MessageId.Value));

    var traced = await new PostgresRouteTraceStore(_database()).GetByMessageIdAsync(envelope.MessageId);

    await Assert.That(traced!.Hops.Single().ServiceInstance).IsEqualTo(envelope.Hops[0].ServiceInstance)
      .Because("a reaped event's trace still rebuilds its hops");
    await Assert.That(((JsonElement)traced.Payload).ValueKind).IsEqualTo(JsonValueKind.Null).Because("its payload is gone with its row");
  }

  [Test]
  public async Task GetCausalChain_StopsAtAMissingParent_AndACycleAsync() {
    await using var conn = await _openAsync();
    var correlation = CorrelationId.New();
    var orphan = _envelope(correlation, MessageId.New(), _t0, 1);
    var a = _envelope(correlation, null, _t0.AddMinutes(2), 3);
    var b = _envelope(correlation, a.MessageId, _t0.AddMinutes(3), 4);
    var cycleStart = new MessageEnvelope<TraceStoreProbePlaced> {
      MessageId = a.MessageId,
      Payload = a.Payload,
      DispatchContext = a.DispatchContext,
      Hops = [a.Hops[0] with { CausationId = b.MessageId }],
    };
    var causationOnly = new MessageEnvelope<TraceStoreProbePlaced> {
      MessageId = MessageId.New(),
      Payload = new TraceStoreProbePlaced(5),
      DispatchContext = a.DispatchContext,
      Hops = [new MessageHop { Type = HopType.Causation, ServiceInstance = StoredMetadataCorpus.Orders, Timestamp = _t0, CausationId = b.MessageId }],
    };
    var memory = new InMemoryTraceStore();
    foreach (var envelope in new[] { orphan, cycleStart, b, causationOnly }) {
      await memory.StoreAsync(envelope);
      await _appendAsync(conn, envelope);
    }
    var postgres = new PostgresRouteTraceStore(_database());

    foreach (var start in new[] { orphan, cycleStart, b }.Select(e => e.MessageId)) {
      await Assert.That(_ids(await postgres.GetCausalChainAsync(start))).IsEquivalentTo(_ids(await memory.GetCausalChainAsync(start)))
        .Because("a missing parent and a cycle each end the walk, as in the in-memory store");
    }
    var chain = await postgres.GetCausalChainAsync(b.MessageId);
    await Assert.That(chain[0].MessageId).IsEqualTo(causationOnly.MessageId)
      .Because("an event with no Current hop sorts first, as the in-memory store sorts it");
  }

  [Test]
  public async Task GetByMessageId_RowsInEveryDriversForm_ComeBackAsync() {
    await using var conn = await _openAsync();
    await _settingAsync(conn, "metadata_write_form", "Slim");
    var hops = StoredMetadataCorpus.HopsJson([StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders)]);
    var dapperForm = await _appendMetadataAsync(conn, $$"""{"message_id":"x","hops":{{hops}}}""");
    var noHopList = await _appendMetadataAsync(conn, """{"k":"v"}""");
    var nullHopList = await _appendMetadataAsync(conn, """{"Hops":null}""");
    var store = new PostgresRouteTraceStore(_database());

    await Assert.That((await store.GetByMessageIdAsync(MessageId.From(dapperForm)))!.Hops.Single().CorrelationId)
      .IsEqualTo(StoredMetadataCorpus.Correlation).Because("the Dapper store's hop list, hops, is read too");
    await Assert.That((await store.GetByMessageIdAsync(MessageId.From(noHopList)))!.Hops).IsEmpty();
    await Assert.That((await store.GetByMessageIdAsync(MessageId.From(nullHopList)))!.Hops).IsEmpty();
  }

  // ===== IRouteStore =====

  [Test]
  public async Task GetRoute_ReturnsTheNormalizedPath_AndItsStatisticsAsync() {
    await using var conn = await _openAsync();
    var hops = new List<MessageHop> { StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders) };
    await _appendRawAsync(conn, ORDER_PLACED, hops);
    await _appendRawAsync(conn, ORDER_PLACED, hops);
    await _executeAsync(conn, "SELECT * FROM wh_distill_event_metadata(1000)");
    var store = new PostgresRouteTraceStore(_database());

    var route = await store.GetRouteAsync(RouteNormalizer.RouteId(ORDER_PLACED, hops));

    await Assert.That(route).IsNotNull();
    await Assert.That(route!.EventType).IsEqualTo("MyApp.Orders.OrderPlaced");
    await Assert.That(route.Occurrences).IsEqualTo(2L);
    var hop = route.Hops.Single();
    await Assert.That(hop.Type).IsEqualTo(HopType.Current);
    await Assert.That(hop.ServiceName).IsEqualTo("orders");
    await Assert.That(hop.Topic).IsEqualTo("orders.events");
    await Assert.That(hop.ExecutionStrategy).IsEqualTo("SerialExecutor");
    await Assert.That(hop.CallerClass).IsEqualTo("PlaceOrderReceptor");
    await Assert.That(hop.CallerMember).IsEqualTo("PlaceAsync");
    await Assert.That(hop.PolicyDecisions).IsEquivalentTo([new RoutePolicyDecision("Tenancy", "tenant-present", true), new RoutePolicyDecision("Routing", "topic-by-namespace", false)]);
    await Assert.That(route.Durations.Count).IsEqualTo(2L);
    await Assert.That(route.Durations.MinMilliseconds).IsEqualTo(12.5);
    await Assert.That(route.Durations.MaxMilliseconds).IsEqualTo(12.5);
    await Assert.That(route.Durations.TotalMilliseconds).IsEqualTo(25d).Because("two occurrences of 12.5 ms each");
    await using (var first = conn.CreateCommand()) {
      first.CommandText = "SELECT min(recorded_at), max(recorded_at) FROM wh_traces";
      await using var reader = await first.ExecuteReaderAsync();
      await reader.ReadAsync();
      await Assert.That(route.FirstSeen).IsEqualTo(reader.GetFieldValue<DateTimeOffset>(0))
        .Because("first seen is when the route's first occurrence was staged");
      await Assert.That(route.LastSeen).IsEqualTo(reader.GetFieldValue<DateTimeOffset>(1));
    }
    await Assert.That(route.Durations.Buckets.Count).IsEqualTo(6);
  }

  [Test]
  public async Task GetRoute_Unknown_IsNullAsync() {
    var store = new PostgresRouteTraceStore(_database());

    await Assert.That(await store.GetRouteAsync((Guid)TrackedGuid.New())).IsNull();
    await Assert.That(() => new PostgresRouteTraceStore(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task GetRoutes_ByEventType_MostTakenFirstAsync() {
    await using var conn = await _openAsync();
    var common = new List<MessageHop> { StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders) };
    var rare = new List<MessageHop> {
      new() { ServiceInstance = StoredMetadataCorpus.Orders, Timestamp = _t0, Topic = "rare" },
      new() { Type = HopType.Causation, ServiceInstance = StoredMetadataCorpus.Billing, Timestamp = _t0 },
    };
    var unknownHop = new List<MessageHop> { new() { ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = _t0 } };
    await _appendRawAsync(conn, ORDER_PLACED, rare);
    await _appendRawAsync(conn, ORDER_PLACED, common);
    await _appendRawAsync(conn, ORDER_PLACED, common);
    await _appendRawAsync(conn, ORDER_SHIPPED, unknownHop);
    await _executeAsync(conn, "SELECT * FROM wh_distill_event_metadata(1000)");
    var store = new PostgresRouteTraceStore(_database());

    var routes = await store.GetRoutesAsync(ORDER_PLACED);

    await Assert.That(routes.Select(r => r.RouteId)).IsEquivalentTo(
      [RouteNormalizer.RouteId(ORDER_PLACED, common), RouteNormalizer.RouteId(ORDER_PLACED, rare)], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(routes[1].Durations.MinMilliseconds).IsNull().Because("a route none of whose occurrences had a duration has no minimum");
    await Assert.That(routes[1].Hops[1].Type).IsEqualTo(HopType.Causation);
    var shipped = (await store.GetRoutesAsync("MyApp.Orders.OrderShipped")).Single();
    await Assert.That(shipped.Hops.Single().ServiceName).IsEqualTo(string.Empty).Because("a hop with no service names none");
    await Assert.That(shipped.Hops.Single().PolicyDecisions).IsEmpty();
    await Assert.That(await store.GetRoutesAsync("MyApp.Nothing")).IsEmpty();
    await Assert.That(async () => await store.GetRoutesAsync(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task GetRoutesForApplyPath_ReturnsEachTypesRoutes_InThePathsOrderAsync() {
    await using var conn = await _openAsync();
    var placed = new List<MessageHop> { StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders) };
    var shipped = new List<MessageHop> { StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Billing) };
    await _appendRawAsync(conn, ORDER_PLACED, placed);
    await _appendRawAsync(conn, ORDER_SHIPPED, shipped);
    await _executeAsync(conn, "SELECT * FROM wh_distill_event_metadata(1000)");
    var store = new PostgresRouteTraceStore(_database());

    var routes = await store.GetRoutesForApplyPathAsync([ORDER_SHIPPED + "+", ORDER_PLACED, ORDER_SHIPPED]);

    await Assert.That(routes.Select(r => r.EventType)).IsEquivalentTo(
      ["MyApp.Orders.OrderShipped", "MyApp.Orders.OrderPlaced"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a run marker is ignored, each type is listed once, in the path's order");
    await Assert.That(async () => await store.GetRoutesForApplyPathAsync(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task RouteStore_SameRouteAndCounts_WhetherTheTraceIsRetainedOrAgedOutAsync() {
    await using var conn = await _openAsync();
    var hops = new List<MessageHop> { StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders) };
    await _appendRawAsync(conn, ORDER_PLACED, hops);
    await _executeAsync(conn, "SELECT * FROM wh_distill_event_metadata(1000)");
    var store = new PostgresRouteTraceStore(_database());
    var routeId = RouteNormalizer.RouteId(ORDER_PLACED, hops);

    var retained = await store.GetRouteAsync(routeId);
    await _executeAsync(conn, "DELETE FROM wh_traces");
    var agedOut = await store.GetRouteAsync(routeId);

    await Assert.That(agedOut!.Occurrences).IsEqualTo(retained!.Occurrences);
    await Assert.That(agedOut.Hops.Single() with { PolicyDecisions = [] }).IsEqualTo(retained.Hops.Single() with { PolicyDecisions = [] })
      .Because("routes never depend on traces");
    await Assert.That(agedOut.Hops.Single().PolicyDecisions).IsEquivalentTo(retained.Hops.Single().PolicyDecisions);
    await Assert.That(agedOut.LastSeen).IsEqualTo(retained.LastSeen);
  }

  // ===== Registration =====

  [Test]
  public async Task Registration_EitherStoreAlone_NeedsNotTheOtherAsync() {
    var routesOnly = _withDatabase().AddWhizbangInMemoryTraceStore().AddWhizbangPostgresRouteStore().BuildServiceProvider();
    var tracesOnly = _withDatabase().AddWhizbangInMemoryTraceStore().AddWhizbangPostgresTraceStore().BuildServiceProvider();

    await Assert.That(routesOnly.GetRequiredService<IRouteStore>()).IsTypeOf<PostgresRouteTraceStore>();
    await Assert.That(routesOnly.GetRequiredService<ITraceStore>()).IsTypeOf<InMemoryTraceStore>()
      .Because("Postgres routes beside the in-memory trace store");
    await Assert.That(tracesOnly.GetRequiredService<ITraceStore>()).IsTypeOf<PostgresRouteTraceStore>()
      .Because("the Postgres trace store replaces one registered before it");
    await Assert.That(tracesOnly.GetServices<ITraceStore>().Count()).IsEqualTo(1);
    await Assert.That(tracesOnly.GetService<IRouteStore>()).IsNull().Because("no route store was asked for");
    await Assert.That(() => ((IServiceCollection)null!).AddWhizbangPostgresRouteStore()).Throws<ArgumentNullException>();
    await Assert.That(() => ((IServiceCollection)null!).AddWhizbangPostgresTraceStore()).Throws<ArgumentNullException>();
  }

  // ===== Helpers =====

  private static List<Guid> _ids(IEnumerable<IMessageEnvelope> envelopes) => [.. envelopes.Select(e => e.MessageId.Value)];

  private IServiceCollection _withDatabase() => new ServiceCollection().AddSingleton(_database());

  private PostgresArchiveConnection _database(string? schema = null) => new(async ct => {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync(ct);
    return connection;
  }, schema);

  private static MessageEnvelope<TraceStoreProbePlaced> _envelope(CorrelationId correlation, MessageId? cause, DateTimeOffset at, int number) =>
    new() {
      MessageId = MessageId.New(),
      Payload = new TraceStoreProbePlaced(number),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [new MessageHop {
        ServiceInstance = StoredMetadataCorpus.Orders,
        Timestamp = at,
        CorrelationId = correlation,
        CausationId = cause,
        Topic = "probe",
      }],
    };

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static Task _appendAsync(NpgsqlConnection conn, MessageEnvelope<TraceStoreProbePlaced> envelope) =>
    _executeAsync(conn, """
      SELECT wh_append_event(@id, @sid, @sid, split_part(@type, ',', 1), @type, @data::jsonb, @meta::jsonb, NULL, now(), 0)
      """,
      ("id", envelope.MessageId.Value), ("sid", (Guid)TrackedGuid.New()), ("type", _probeType),
      ("data", $$"""{"Number":{{envelope.Payload.Number}}}"""),
      ("meta", $$"""{"MessageId":"{{envelope.MessageId.Value}}","Hops":{{StoredMetadataCorpus.HopsJson(envelope.Hops)}}}"""));

  private static Task _appendRawAsync(NpgsqlConnection conn, string eventType, List<MessageHop> hops) {
    var id = (Guid)TrackedGuid.New();
    return _executeAsync(conn, """
      SELECT wh_append_event(@id, @sid, @sid, split_part(@type, ',', 1), @type, '{"amount": 1}'::jsonb, @meta::jsonb, NULL, now(), 0)
      """,
      ("id", id), ("sid", (Guid)TrackedGuid.New()), ("type", eventType),
      ("meta", $$"""{"MessageId":"{{id}}","Hops":{{StoredMetadataCorpus.HopsJson(hops)}}}"""));
  }

  private static async Task<Guid> _appendMetadataAsync(NpgsqlConnection conn, string metadataJson) {
    var id = (Guid)TrackedGuid.New();
    await _executeAsync(conn, """
      SELECT wh_append_event(@id, @sid, @sid, 'X', 'X, Y', '{"amount": 1}'::jsonb, @meta::jsonb, NULL, now(), 0)
      """, ("id", id), ("sid", (Guid)TrackedGuid.New()), ("meta", metadataJson));
    return id;
  }

  private static Task _settingAsync(NpgsqlConnection conn, string key, string value) =>
    _executeAsync(conn, """
      INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES (@k, @v, 'string')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
      """, ("k", key), ("v", value));

  private static async Task<long> _countAsync(NpgsqlConnection conn, string countSql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = countSql;
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
