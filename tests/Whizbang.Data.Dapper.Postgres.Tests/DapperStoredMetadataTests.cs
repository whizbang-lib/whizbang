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
using Whizbang.Core.Data;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Dapper.Custom;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Dapper event store's direct append (#1725): through <c>wh_append_event</c>, the row keeps the slim form with its
/// route id and, under the default Linked form, the metadata as written is staged. The SQL itself is covered by the
/// EF Core <c>StoredMetadataWriteSqlTests</c>; this locks the driver's side.
/// </summary>
/// <docs>data/event-store#stored-metadata</docs>
public class DapperStoredMetadataTests : PostgresTestBase {
  private static readonly DateTimeOffset _sent = new(2026, 3, 1, 9, 30, 0, TimeSpan.Zero);

  [Test]
  public async Task AppendAsync_StoresTheSlimRow_AndStagesTheFullFormAsync() {
    var store = _eventStore();
    var stream = (Guid)TrackedGuid.New();
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo { ServiceName = "orders", InstanceId = (Guid)TrackedGuid.New(), HostName = "host", ProcessId = 7 },
      Timestamp = _sent,
      Topic = "orders.events",
      CallerMemberName = "PlaceAsync",
      CallerFilePath = "/src/Orders/PlaceOrderReceptor.cs",
      CallerLineNumber = 12,
      TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
    };
    var envelope = new MessageEnvelope<DapperMixedStreamProbeNoted> {
      MessageId = MessageId.From(TrackedGuid.New()),
      Payload = new DapperMixedStreamProbeNoted { StreamId = stream },
      Hops = [hop],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };

    await store.AppendAsync(stream, envelope);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var id = envelope.MessageId.Value;
    using var row = JsonDocument.Parse(await conn.ExecuteScalarAsync<string>("SELECT metadata::text FROM wh_event_body WHERE event_id = @id", new { id }) ?? "{}");
    var slimHop = row.RootElement.GetProperty("hops").EnumerateArray().Single();
    await Assert.That(slimHop.TryGetProperty("si", out _)).IsFalse().Because("the slim hop names no service instance");
    await Assert.That(row.RootElement.GetProperty("message_id").GetString()).IsEqualTo(id.ToString())
      .Because("the Dapper store reads its own form back, so its keys are kept");
    await Assert.That(slimHop.GetProperty("tp").GetString()).IsEqualTo(hop.TraceParent);
    var routeId = RouteNormalizer.RouteId(TypeNameFormatter.Format(typeof(DapperMixedStreamProbeNoted)), envelope.Hops);
    await Assert.That(row.RootElement.GetProperty("rid").GetGuid()).IsEqualTo(routeId)
      .Because("the route the C# normalizer names is the one stamped on the row");
    var stagedHopService = await conn.ExecuteScalarAsync<string>(
      "SELECT metadata -> 'hops' -> 0 -> 'si' ->> 'sn' FROM wh_event_metadata_staging WHERE event_id = @id AND route_id = @routeId", new { id, routeId });
    await Assert.That(stagedHopService).IsEqualTo("orders").Because("the staged form keeps the full hop");
  }

  [Test]
  public async Task AddWhizbangPostgres_RegistersTheHistoryConversionStep_ThatConvertsTheStoresOwnFormAsync() {
    var stream = (Guid)TrackedGuid.New();
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo { ServiceName = "orders", InstanceId = (Guid)TrackedGuid.New(), HostName = "host", ProcessId = 7 },
      Timestamp = _sent,
      Topic = "orders.events",
      CallerMemberName = "PlaceAsync",
      CallerFilePath = "/src/Orders/PlaceOrderReceptor.cs",
      CallerLineNumber = 12,
    };
    var envelope = new MessageEnvelope<DapperMixedStreamProbeNoted> {
      MessageId = MessageId.From(TrackedGuid.New()),
      Payload = new DapperMixedStreamProbeNoted { StreamId = stream },
      Hops = [hop],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };
    var id = envelope.MessageId.Value;
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    // A row as the store wrote it before slim metadata: the full form, and no route id.
    _ = await conn.ExecuteAsync("INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES ('metadata_write_form', 'Full', 'string')");
    await _eventStore().AppendAsync(stream, envelope);
    _ = await conn.ExecuteAsync("DELETE FROM wh_settings WHERE setting_key = 'metadata_write_form'");
    _ = await conn.ExecuteAsync("UPDATE wh_event_body SET metadata = metadata - 'rid' WHERE event_id = @id", new { id });
    var services = new ServiceCollection().AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), false, (string?)null, null);
    services.AddLogging();
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    var steps = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<MetadataHistoryConversionStep>().ToList();
    await steps.Single().RunAsync(scope.ServiceProvider, CancellationToken.None);

    await Assert.That(steps).Count().IsEqualTo(1).Because("history is converted by the maintenance cycle, under the maintainer duty, on either driver");
    using var row = JsonDocument.Parse(await conn.ExecuteScalarAsync<string>("SELECT metadata::text FROM wh_event_body WHERE event_id = @id", new { id }) ?? "{}");
    await Assert.That(row.RootElement.GetProperty("hops").EnumerateArray().Single().TryGetProperty("si", out _)).IsFalse()
      .Because("the store's own hop list (hops) is reduced to the slim hop");
    await Assert.That(row.RootElement.GetProperty("message_id").GetString()).IsEqualTo(id.ToString())
      .Because("the store reads its own keys back, so they are kept");
    await Assert.That(row.RootElement.GetProperty("rid").GetGuid())
      .IsEqualTo(RouteNormalizer.RouteId(TypeNameFormatter.Format(typeof(DapperMixedStreamProbeNoted)), envelope.Hops));
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
}
