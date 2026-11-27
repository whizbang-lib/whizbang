// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
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

/// <summary>A vector event of the Dapper driver's tests: its generated context registers the path <c>Embedding</c>.</summary>
public sealed record DapperPackedVectorNoted : IEvent {
  /// <summary>The stream.</summary>
  [StreamId]
  public Guid StreamId { get; init; }

  /// <summary>The vector.</summary>
  public float[] Embedding { get; init; } = [];
}

/// <summary>
/// #2625 on the Dapper driver: <c>AddWhizbangPostgres</c> registers the packing gate step, its cycle writes the
/// service's vector paths and opens the gate, and the store's direct append then stores the vector packed, which the
/// store reads back bit-exact. The SQL itself is covered by the EF Core <c>PackedVectorStorageSqlTests</c>.
/// </summary>
/// <docs>data/event-store#packing-gate</docs>
public class DapperPackedVectorTests : PostgresTestBase {
  [Test]
  public async Task AddWhizbangPostgres_RegistersThePackingGateStep_AndTheDirectAppendPacksOnceItOpensAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    _ = await conn.ExecuteAsync("SELECT record_heartbeat(@i, 'dapper-svc', 'this-build', 1, '{\"stored_form_level\": 2}'::jsonb)",
      new { i = (Guid)TrackedGuid.New() });
    var services = new ServiceCollection().AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), false, (string?)null, null);
    services.AddLogging();
    services.AddSingleton<ILibraryVersionProvider>(new LibraryVersionProvider("2.0.0"));
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider((Guid)TrackedGuid.New(), "dapper-svc", "host", 1));
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    var all = scope.ServiceProvider.GetServices<IMaintenanceStep>().ToList();
    var steps = all.OfType<VectorPackingGateStep>().ToList();
    await Assert.That(steps).Count().IsEqualTo(1);
    await Assert.That(all.FindIndex(s => s is VectorPackingGateStep)).IsGreaterThan(all.FindIndex(s => s is MetadataTrimGateStep));
    await steps.Single().RunAsync(scope.ServiceProvider, CancellationToken.None);

    var stream = (Guid)TrackedGuid.New();
    float[] vector = [0.1f, -2.5f, float.Epsilon, float.MaxValue];
    var envelope = new MessageEnvelope<DapperPackedVectorNoted> {
      MessageId = MessageId.From(TrackedGuid.New()),
      Payload = new DapperPackedVectorNoted { StreamId = stream, Embedding = vector },
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };
    var store = _eventStore();
    await store.AppendAsync(stream, envelope);

    var body = JsonNode.Parse(await conn.ExecuteScalarAsync<string>(
      "SELECT event_data::text FROM wh_event_body WHERE event_id = @id", new { id = envelope.MessageId.Value }) ?? "{}")!;
    await Assert.That(body["Embedding"]!.GetValueKind()).IsEqualTo(JsonValueKind.String).Because("the gate is open, so the append packs");
    var read = new List<MessageEnvelope<DapperPackedVectorNoted>>();
    await foreach (var e in store.ReadAsync<DapperPackedVectorNoted>(stream, 0L)) {
      read.Add(e);
    }
    await Assert.That(read.Single().Payload.Embedding.Select(BitConverter.SingleToUInt32Bits))
      .IsEquivalentTo(vector.Select(BitConverter.SingleToUInt32Bits));
  }

  [Test]
  public async Task AddWhizbangPostgres_RegistersTheStoredVectorReadersStep_WhichRefusesAPackedPathThisBuildNoLongerReadsAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var services = new ServiceCollection().AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), false, (string?)null, null);
    services.AddLogging();
    await using var provider = services.BuildServiceProvider();
    var step = provider.GetServices<Whizbang.Core.Startup.IStartupStep>().OfType<StoredVectorReadersStartupStep>().Single();

    var clean = await step.ExecuteAsync(CancellationToken.None);
    _ = await conn.ExecuteAsync("INSERT INTO wh_packed_vector_paths (event_type, path) VALUES (@t, '[\"Retired\"]'::jsonb)",
      new { t = TypeNameFormatter.Format(typeof(DapperPackedVectorNoted)) });
    var failure = await Assert.That(async () => await step.ExecuteAsync(CancellationToken.None)).Throws<PackedVectorsNoLongerReadException>();

    await Assert.That(clean.Outcome).IsEqualTo(Whizbang.Core.Startup.StartupStepOutcome.Completed);
    await Assert.That(failure!.Paths).IsEquivalentTo([$"{TypeNameFormatter.Format(typeof(DapperPackedVectorNoted))}: Retired"]);
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
