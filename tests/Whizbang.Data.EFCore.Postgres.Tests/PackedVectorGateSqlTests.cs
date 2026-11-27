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
using Whizbang.Core.Versioning;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2625: the packing gate. The write paths pack vectors only while the packed-vectors conversion is journaled
/// (<c>wh_stored_form_migrations</c> row <c>wh_event_body:packed-vectors</c>), and the <c>vector-packing-gate</c>
/// maintenance step journals it only once every live instance's heartbeat says stored form level 2 or higher. The
/// journal row is the switch, so nothing can pack without it, and it records the library version that started packing,
/// so the rollback gate (#2022) stands down an older library afterward, naming packed vectors. The step also writes this
/// service's vector paths, from the generated registry, before it asks.
/// </summary>
/// <docs>data/event-store#packing-gate</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/720_VectorEventBodiesArePackedOnceEveryInstanceReadsThem.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/VectorPackingGateStep.cs</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class PackedVectorGateSqlTests : EFCoreTestBase {
  private const string SERVICE = "vector-svc";
  private const string NEWER_LIBRARY = "1000.0.0";
  private const string JOURNAL_SQL = """
    SELECT count(*) FROM wh_stored_form_migrations WHERE name = 'wh_event_body:packed-vectors'
    """;
  private static readonly JsonSerializerOptions _json = JsonContextRegistry.CreateCombinedOptions();

  // ===== The gate =====

  [Test]
  public async Task Gate_AFreshService_OpensOnlyOnceThisInstanceHasRegisteredWithItsMarkerAsync() {
    await using var conn = await _openAsync();

    var empty = await _gateAsync();
    await _registerThisBuildAsync();
    var registered = await _gateAsync();
    var again = await _gateAsync();

    await Assert.That(empty).IsEqualTo("closed (0 of 0 live instances read packed vectors)")
      .Because("an empty registry has no instance that says it reads packed vectors");
    await Assert.That(registered).IsEqualTo(VectorPackingGateStep.OPENED);
    await Assert.That(again).IsEqualTo("open");
    await Assert.That(await _scalarAsync<long>(conn, JOURNAL_SQL)).IsEqualTo(1L).Because("the conversion is journaled once");
  }

  [Test]
  [Arguments("gone")]
  [Arguments("stale")]
  [Arguments("raised")]
  public async Task Gate_StaysClosedWhileALiveInstanceReadsNoPackedVectors_AndOpensOnceItIsGoneStaleOrRaisedAsync(string how) {
    await using var conn = await _openAsync();
    await _registerThisBuildAsync();
    var older = await _registerAnEarlierReleaseAsync(conn);

    var first = await _gateAsync();
    var again = await _gateAsync();
    await Assert.That(await _scalarAsync<long>(conn, JOURNAL_SQL)).IsEqualTo(0L).Because("while closed, nothing is journaled");
    var change = how switch {
      "gone" => "DELETE FROM wh_service_instances WHERE instance_id = @i",
      "stale" => "UPDATE wh_service_instances SET last_heartbeat_at = now() - interval '1 hour' WHERE instance_id = @i",
      _ => "UPDATE wh_service_instances SET metadata = metadata || '{\"stored_form_level\": 2}'::jsonb WHERE instance_id = @i",
    };
    await _executeAsync(conn, change, ("i", older));
    var after = await _gateAsync();

    await Assert.That(first).IsEqualTo("closed (1 of 2 live instances read packed vectors)");
    await Assert.That(again).IsEqualTo(first).Because("asking again changes nothing while that instance is live");
    await Assert.That(after).IsEqualTo(VectorPackingGateStep.OPENED).Because($"the instance is {how}");
  }

  [Test]
  public async Task Gate_OnceOpen_StaysOpen_WhenAnInstanceWithoutTheMarkerAppearsAsync() {
    await using var conn = await _openAsync();
    await _registerThisBuildAsync();
    await Assert.That(await _gateAsync()).IsEqualTo(VectorPackingGateStep.OPENED).Because("control");

    await _registerAnEarlierReleaseAsync(conn);

    await Assert.That(await _gateAsync()).IsEqualTo("open").Because("the latch is one-way: packed bodies may already exist");
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT _wh_vector_packing_on()")).IsTrue();
  }

  [Test]
  public async Task Gate_WithoutALibraryVersion_StaysClosed_SoNothingPacksUnjournaledAsync() {
    await using var conn = await _openAsync();
    await _registerThisBuildAsync();

    var status = await _gateAsync(library: null);

    await Assert.That(status).IsEqualTo("closed (this instance's library version is unknown, so the conversion cannot be journaled)");
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT _wh_vector_packing_on()")).IsFalse();
  }

  [Test]
  public async Task Switch_IsTheJournalRow_ARowWithNoApplierDoesNotOpenItAsync() {
    await using var conn = await _openAsync();
    await _executeAsync(conn, """
      INSERT INTO wh_stored_form_migrations (name, table_name, kind) VALUES ('wh_event_body:packed-vectors', 'wh_event_body', 'library')
      """);

    await Assert.That(await _scalarAsync<bool>(conn, "SELECT _wh_vector_packing_on()")).IsFalse()
      .Because("only a conversion with a known applier, which the rollback gate can judge, opens the write paths");
  }

  [Test]
  public async Task Journal_RecordsTheLibraryThatStartedPacking_AndNoApplicationVersionAsync() {
    await using var conn = await _openAsync();
    await _registerThisBuildAsync();
    await _gateAsync(library: "2.0.0");

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT table_name, kind, applied_library_version, applied_application_version, first_applied_at IS NOT NULL
      FROM wh_stored_form_migrations WHERE name = 'wh_event_body:packed-vectors'
      """;
    await using var reader = await cmd.ExecuteReaderAsync();
    await Assert.That(await reader.ReadAsync()).IsTrue();
    await Assert.That(reader.GetString(0)).IsEqualTo("wh_event_body");
    await Assert.That(reader.GetString(1)).IsEqualTo("library");
    await Assert.That(reader.GetString(2)).IsEqualTo("2.0.0");
    await Assert.That(reader.IsDBNull(3)).IsTrue()
      .Because("reading packed vectors is a library capability: an application rolled back on the same library reads them");
    await Assert.That(reader.GetBoolean(4)).IsTrue();
  }

  // ===== The rollback gate =====

  [Test]
  [Timeout(60000)]
  public async Task Rollback_AnOlderLibrary_StandsDownAtAssess_NamingPackedVectorsAsync(CancellationToken ct) {
    await _registerThisBuildAsync();
    await _gateAsync(library: NEWER_LIBRARY);
    var library = (await _libraryAsync(ct))!;

    var assessment = await _assessor(library, "1.0.0").AssessAsync(ct);

    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.StandDown);
    await Assert.That(assessment.Reason)
      .Contains($"stored-form:wh_event_body:packed-vectors (applied by library {NEWER_LIBRARY}, application unknown)");
  }

  [Test]
  [Timeout(60000)]
  public async Task Rollback_AnOlderApplicationOnTheSameLibrary_IsNotStoodDownByPackingAsync(CancellationToken ct) {
    var library = (await _libraryAsync(ct))!;
    await _registerThisBuildAsync();
    await _gateAsync(library: library);

    var assessment = await _assessor(library, "0.0.1").AssessAsync(ct);

    await Assert.That(assessment.Verdict).IsNotEqualTo(StartupVerdict.StandDown).Because(assessment.Reason);
  }

  // ===== The paths =====

  [Test]
  public async Task Paths_TheStepWritesThisServicesGeneratedPaths_AndReplacesOnlyItsOwnAsync() {
    await using var conn = await _openAsync();
    await _executeAsync(conn, """
      INSERT INTO wh_event_vector_paths (event_type, paths, service_name) VALUES
        ('Gone.Type, Gone', '[["Old"]]', @svc),
        ('Other.Type, Other', '[["Theirs"]]', 'other-svc'),
        (@sample, '[["Stale"]]', 'other-svc')
      """, ("svc", SERVICE), ("sample", TypeNameFormatter.Format(typeof(PackedVectorSampleEvent))));

    _ = await _gateAsync();

    await Assert.That(await _scalarAsync<bool>(conn, """
      SELECT paths @> '[["Embedding"], ["Lead", "Embedding"]]' AND jsonb_array_length(paths) = 2
      FROM wh_event_vector_paths WHERE event_type = @t
      """, ("t", TypeNameFormatter.Format(typeof(PackedVectorSampleEvent)))))
      .IsTrue().Because("the generated registry's paths, which replace another's");
    await Assert.That(await _scalarAsync<string>(conn, "SELECT service_name FROM wh_event_vector_paths WHERE event_type = @t",
      ("t", TypeNameFormatter.Format(typeof(PackedVectorSampleEvent))))).IsEqualTo(SERVICE).Because("the latest registration wins");
    await Assert.That(await _pathsAsync(conn, "Gone.Type, Gone")).IsNull().Because("this service no longer declares it");
    await Assert.That(await _pathsAsync(conn, "Other.Type, Other")).IsEqualTo("""[["Theirs"]]""")
      .Because("another service's registration is its own");
    await Assert.That(await _pathsAsync(conn, TypeNameFormatter.Format(typeof(PackedVectorOrdinaryEvent)))).IsNull()
      .Because("a type without vectors has no row");
  }

  [Test]
  public async Task Paths_WithoutAServiceName_AreNotWritten_AndTheGateStillAnswersAsync() {
    await using var conn = await _openAsync();
    await _registerThisBuildAsync();

    var status = await _step().OpenAsync(_services("2.0.0", withInstance: false), CancellationToken.None);

    await Assert.That(status).IsEqualTo(VectorPackingGateStep.OPENED);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_event_vector_paths")).IsEqualTo(0L);
  }

  [Test]
  public async Task Paths_TheRegistrationRefusesAnEntryWithoutPathsAsync() {
    await using var conn = await _openAsync();

    await Assert.That(async () => await _executeAsync(conn,
      "SELECT wh_register_event_vector_paths('svc', '[{\"t\": \"A, B\", \"p\": []}]'::jsonb)"))
      .Throws<PostgresException>();
  }

  [Test]
  public async Task Step_IsNamed_AndRunsTheGateAsync() {
    await using var conn = await _openAsync();
    await _registerThisBuildAsync();
    var step = VectorPackingGateStep.FromServices(_services("2.0.0"));

    // No schema: the connection's own, unqualified, as the trim gate's step reads it.
    await step.RunAsync(_services("2.0.0", schema: null), CancellationToken.None);

    await Assert.That(step.Name).IsEqualTo("vector-packing-gate");
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT _wh_vector_packing_on()")).IsTrue();
    await Assert.That(VectorPackingGateStep.FromServices(new ServiceCollection().BuildServiceProvider()).Name)
      .IsEqualTo("vector-packing-gate");
    await Assert.That(() => VectorPackingGateStep.FromServices(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new VectorPackingGateStep(null!)).Throws<ArgumentNullException>();
    await Assert.That(async () => await step.OpenAsync(null!, CancellationToken.None)).Throws<ArgumentNullException>();
  }

  // ===== Helpers =====

  private EFCorePostgresStartupAssessor _assessor(string library, string? application) {
    var services = new ServiceCollection();
    services.AddScoped(_ => CreateDbContext());
    return new EFCorePostgresStartupAssessor(
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), typeof(WorkCoordinationDbContext),
      new LibraryVersionProvider(library), new ApplicationVersionProvider(ApplicationVersion.FromRecorded(application)));
  }

  private async Task<string?> _libraryAsync(CancellationToken ct) {
    await using var conn = await _openAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT v.library_version FROM wh_schema_migrations m JOIN wh_schema_versions v ON v.id = m.applied_version_id
      WHERE m.owner = 'whizbang' ORDER BY m.file_name DESC LIMIT 1
      """;
    return (string?)await cmd.ExecuteScalarAsync(ct);
  }

  private static Task<string?> _pathsAsync(NpgsqlConnection conn, string type) =>
    _scalarAsync<string?>(conn, "SELECT paths::text FROM wh_event_vector_paths WHERE event_type = @t", ("t", type));

  private ServiceProvider _services(string? library, bool withInstance = true, string? schema = "public") {
    var services = new ServiceCollection()
      .AddSingleton(new PostgresArchiveConnection(async ct => {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
      }, schema));
    if (library is not null) {
      services.AddSingleton<ILibraryVersionProvider>(new LibraryVersionProvider(library));
    }
    if (withInstance) {
      services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider((Guid)TrackedGuid.New(), SERVICE, "host", 1));
    }
    return services.BuildServiceProvider();
  }

  private static VectorPackingGateStep _step() => new(NullLogger<VectorPackingGateStep>.Instance);

  private async Task<string> _gateAsync(string? library = "2.0.0") =>
    await _step().OpenAsync(_services(library), CancellationToken.None);

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
