// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2625: a vector path that has packed bodies stays readable. Every path registered while the packing gate is open is
/// recorded in <c>wh_packed_vector_paths</c> and never dropped by a later registration, and the blocking
/// <c>StoredVectorReaders</c> startup step refuses to make an instance ready when an event type it reads has a packed
/// path its build no longer reads as a vector (an opted-out, retyped or removed property): a body there would fail to
/// read, so the instance stops loudly at startup, naming the type and the property, instead.
/// </summary>
/// <docs>data/event-store#packed-vectors-no-longer-read</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/StoredVectorReadersStartupStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/720_VectorEventBodiesArePackedOnceEveryInstanceReadsThem.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class PackedVectorReaderGuardSqlTests : EFCoreTestBase {
  private const string SERVICE = "vector-svc";
  private const string LEDGER_SQL = "SELECT count(*) FROM wh_packed_vector_paths WHERE event_type = @t AND path = @p::jsonb";
  private static readonly JsonSerializerOptions _json = JsonContextRegistry.CreateCombinedOptions();
  private static readonly string _sampleType = TypeNameFormatter.Format(typeof(PackedVectorSampleEvent));

  // ===== The record of packed paths =====

  [Test]
  public async Task Ledger_RecordsTheRegisteredPaths_OnlyOnceTheGateOpens_AndKeepsThemWhenARegistrationDropsThemAsync() {
    await using var conn = await _openAsync();
    var earlier = await _registerAnEarlierReleaseAsync(conn);
    await _registerThisBuildAsync();
    await Assert.That(await _gateAsync()).StartsWith("closed").Because("control: an earlier release is live");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_packed_vector_paths")).IsEqualTo(0L)
      .Because("nothing is packed while the gate is closed, so no path is recorded");

    await _executeAsync(conn, "DELETE FROM wh_service_instances WHERE instance_id = @i", ("i", earlier));
    await Assert.That(await _gateAsync()).IsEqualTo(VectorPackingGateStep.OPENED);
    var atOpen = await _scalarAsync<long>(conn, LEDGER_SQL, ("t", _sampleType), ("p", """["Lead", "Embedding"]"""));
    await _executeAsync(conn, "SELECT wh_register_event_vector_paths(@s, @e::jsonb)",
      ("s", SERVICE), ("e", $$"""[{"t": "{{_sampleType}}", "p": [["Embedding"]]}, {"t": "Later.Type, Later", "p": [["V"]]}]"""));

    await Assert.That(atOpen).IsEqualTo(1L).Because("the paths registered before the gate opened are recorded as it opens");
    await Assert.That(await _scalarAsync<long>(conn, LEDGER_SQL, ("t", _sampleType), ("p", """["Lead", "Embedding"]""")))
      .IsEqualTo(1L).Because("a registration that drops a path never drops its record: its bodies are still packed");
    await Assert.That(await _scalarAsync<long>(conn, LEDGER_SQL, ("t", "Later.Type, Later"), ("p", """["V"]""")))
      .IsEqualTo(1L).Because("a path registered while the gate is open is recorded before anything packs it");
  }

  // ===== The startup guard =====

  [Test]
  public async Task Startup_APackedPathThisBuildNoLongerReads_FailsNamingTheTypeAndThePropertyAsync() {
    await using var conn = await _openAsync();
    await _openTheGateAsync();
    // A property packed by an earlier build, since opted out with [StoredAsJsonArray]: this build reads it as numbers only.
    await _executeAsync(conn, "INSERT INTO wh_packed_vector_paths (event_type, path) VALUES (@t, '[\"Centroid\"]'::jsonb)", ("t", _sampleType));
    // And a nested vector since removed from the chunk type.
    await _executeAsync(conn, "INSERT INTO wh_packed_vector_paths (event_type, path) VALUES (@t, '[\"Lead\", \"Retired\"]'::jsonb)", ("t", _sampleType));

    var failure = await Assert.That(async () => await _step().ExecuteAsync(CancellationToken.None))
      .Throws<PackedVectorsNoLongerReadException>();

    await Assert.That(failure!.Message).Contains(_sampleType);
    await Assert.That(failure.Message).Contains("Centroid");
    await Assert.That(failure.Message).Contains("[VectorField]");
    await Assert.That(failure.Message).Contains("unpack");
    await Assert.That(failure.Message).Contains("Lead.Retired");
    await Assert.That(failure.Paths).IsEquivalentTo([$"{_sampleType}: Centroid", $"{_sampleType}: Lead.Retired"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Startup_EveryPackedPathStillRead_Completes_AndAPathOfATypeThisServiceDoesNotReadIsNotItsConcernAsync() {
    await using var conn = await _openAsync();
    await _openTheGateAsync();
    await _executeAsync(conn, "INSERT INTO wh_packed_vector_paths (event_type, path) VALUES ('Some.Other, Elsewhere', '[\"Gone\"]'::jsonb)");

    var report = await _step().ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Completed).Because(report.Reason ?? "no reason");
  }

  [Test]
  public async Task Startup_ATypeTheCatalogNamesWithNoVectorLeft_IsCheckedTooAsync() {
    await using var conn = await _openAsync();
    await _openTheGateAsync();
    var ordinary = TypeNameFormatter.Format(typeof(PackedVectorOrdinaryEvent));
    await _executeAsync(conn, "INSERT INTO wh_packed_vector_paths (event_type, path) VALUES (@t, '[\"Amount\"]'::jsonb)", ("t", ordinary));

    var failure = await Assert.That(async () => await _step(new OneEventCatalog(typeof(PackedVectorOrdinaryEvent))).ExecuteAsync(CancellationToken.None))
      .Throws<PackedVectorsNoLongerReadException>();
    var withoutCatalog = await _step().ExecuteAsync(CancellationToken.None);

    await Assert.That(failure!.Paths).IsEquivalentTo([$"{ordinary}: Amount"])
      .Because("a type whose every vector was removed registers no path, so the catalog is what says this service reads it");
    await Assert.That(withoutCatalog.Outcome).IsEqualTo(StartupStepOutcome.Completed)
      .Because("without a catalog the service is known to read only the types that register vector paths");
  }

  [Test]
  public async Task Step_IsABlockingStepAfterMigrate_OnEveryInstanceAsync() {
    var step = _step();

    await Assert.That(step.Descriptor.Name).IsEqualTo(StoredVectorReadersStartupStep.STEP_NAME);
    await Assert.That(step.Descriptor.DependsOn).IsEquivalentTo([FrameworkStartupSteps.MIGRATE]);
    await Assert.That(step.Descriptor.Blocking).IsTrue().Because("an instance that cannot read its stored bodies must never become ready");
    await Assert.That(step.Descriptor.RequiredCapability).IsEqualTo(StartupCapabilities.EVERY_INSTANCE);
    await Assert.That(() => new StoredVectorReadersStartupStep(null!, null, NullLogger<StoredVectorReadersStartupStep>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new StoredVectorReadersStartupStep(_services(), null, null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new PackedVectorsNoLongerReadException((IReadOnlyList<string>)null!)).Throws<ArgumentNullException>();
    var inner = new InvalidOperationException("cause");
    await Assert.That(new PackedVectorsNoLongerReadException().Paths).IsEmpty();
    await Assert.That(new PackedVectorsNoLongerReadException("m").Message).IsEqualTo("m");
    await Assert.That(new PackedVectorsNoLongerReadException("m", inner).InnerException).IsSameReferenceAs(inner);
  }

  // ===== Helpers =====

  private sealed class OneEventCatalog(Type type) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() =>
      [new(type, TypeNameFormatter.Format(type), "event", null), new(typeof(string), "System.String", "command", null)];
  }

  private StoredVectorReadersStartupStep _step(IMessageTypeCatalog? catalog = null) =>
    new(_services(), catalog, NullLogger<StoredVectorReadersStartupStep>.Instance);

  private ServiceProvider _services() =>
    new ServiceCollection()
      .AddSingleton(new PostgresArchiveConnection(async ct => {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
      }, "public"))
      .AddSingleton<ILibraryVersionProvider>(new LibraryVersionProvider("2.0.0"))
      .AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider((Guid)TrackedGuid.New(), SERVICE, "host", 1))
      .BuildServiceProvider();

  private async Task<string> _gateAsync() =>
    await new VectorPackingGateStep(NullLogger<VectorPackingGateStep>.Instance).OpenAsync(_services(), CancellationToken.None);

  private async Task _openTheGateAsync() {
    await _registerThisBuildAsync();
    await Assert.That(await _gateAsync()).IsEqualTo(VectorPackingGateStep.OPENED);
  }

  private async Task _registerThisBuildAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, _json);
    await Assert.That(await coordinator.RecordHeartbeatAsync(new HeartbeatRequest((Guid)TrackedGuid.New(), SERVICE, "this-build", 1)))
      .IsTrue();
  }

  private static async Task<Guid> _registerAnEarlierReleaseAsync(NpgsqlConnection conn) {
    var id = (Guid)TrackedGuid.New();
    await _executeAsync(conn, "SELECT record_heartbeat(@i, 'vector-svc', 'earlier-release', 2, '{\"stored_form_level\": 1}'::jsonb)", ("i", id));
    return id;
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    var result = await cmd.ExecuteScalarAsync();
    return result is DBNull or null ? default! : (T)result;
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
