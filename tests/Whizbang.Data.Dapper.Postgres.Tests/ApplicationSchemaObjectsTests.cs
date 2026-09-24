using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Data;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The ordering contract for an application's own SQL objects.
/// </summary>
/// <remarks>
/// Being able to run SQL was never the hard part. The guarantee is where in the sequence it runs,
/// so what is asserted here is the order relative to the perspective pass: an object in the first
/// slot exists before any perspective table does, which is what an immutable function behind an
/// expression index needs, and one in the second slot may refer to a perspective's table, which it
/// could not if it ran any earlier.
/// </remarks>
/// <docs>operations/infrastructure/migrations</docs>
[Category("Integration")]
public class ApplicationSchemaObjectsTests : IAsyncDisposable {
  private string? _databaseName;
  private string _connectionString = null!;

  private sealed record Objects(
      IReadOnlyList<ApplicationSchemaObject> BeforePerspectives,
      IReadOnlyList<ApplicationSchemaObject> AfterPerspectives) : IApplicationSchemaObjects;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _databaseName = $"appobj_{Guid.NewGuid():N}";

    await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await admin.OpenAsync();
    await admin.ExecuteAsync($"CREATE DATABASE {_databaseName}");

    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
    }.ConnectionString;
  }

  [After(Test)]
  public async ValueTask DisposeAsync() {
    if (_databaseName is not null) {
      try {
        await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)");
      } catch (NpgsqlException) {
        // The container is shared and the database is per-test; a failed drop is not a test failure.
      }
    }

    GC.SuppressFinalize(this);
  }

  /// <summary>
  /// An object in the first slot is there before the perspective pass, and one in the second can
  /// refer to what that pass created.
  /// </summary>
  [Test]
  [Timeout(180000)]
  public async Task BothSlotsApply_InTheOrderTheContractStatesAsync(CancellationToken cancellationToken) {
    // Depends on nothing, and the perspective below depends on it: an index over a call to this
    // function cannot be created unless the function already exists.
    var folding = new ApplicationSchemaObject("fold_label", """
      CREATE OR REPLACE FUNCTION fold_label(value text) RETURNS text
        LANGUAGE sql IMMUTABLE AS $$ SELECT lower(btrim(value)) $$;
      """);

    // Refers to a perspective's table, so it cannot be created any earlier than the second slot.
    var view = new ApplicationSchemaObject("probe_view", """
      CREATE OR REPLACE VIEW probe_view AS SELECT id FROM wh_per_probe;
      """);

    var perspective = new KeyValuePair<string, string>("Probe", """
      CREATE TABLE IF NOT EXISTS wh_per_probe (
        id uuid PRIMARY KEY,
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL,
        version integer NOT NULL,
        label text
      );
      CREATE INDEX IF NOT EXISTS ix_probe_folded ON wh_per_probe (fold_label(label));
      """);

    var initializer = new PostgresSchemaInitializer(
      _connectionString,
      [perspective],
      migrationProvider: null,
      applicationVersion: null,
      applicationObjects: new Objects([folding], [view]));

    await initializer.InitializeSchemaAsync(cancellationToken);

    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync(cancellationToken);

    var index = await db.ExecuteScalarAsync<long>(
      "SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_probe_folded'");
    await Assert.That(index).IsEqualTo(1L)
      .Because("the index calls the function, so it could only be created if the function came first");

    var probeView = await db.ExecuteScalarAsync<long>(
      "SELECT count(*) FROM pg_views WHERE viewname = 'probe_view'");
    await Assert.That(probeView).IsEqualTo(1L)
      .Because("the view selects from the perspective's table, so it could only be created after it");
  }

  /// <summary>
  /// Applying again is silent, and an object whose SQL has not changed is skipped.
  /// </summary>
  /// <remarks>
  /// The sequence runs on every start, so contributing an object has to cost nothing afterwards.
  /// </remarks>
  [Test]
  [Timeout(180000)]
  public async Task AnUnchangedObjectIsSkippedOnTheNextStartAsync(CancellationToken cancellationToken) {
    var owned = new ApplicationSchemaObject("fold_label", """
      CREATE OR REPLACE FUNCTION fold_label(value text) RETURNS text
        LANGUAGE sql IMMUTABLE AS $$ SELECT lower(btrim(value)) $$;
      """);

    var objects = new Objects([owned], []);

    await new PostgresSchemaInitializer(_connectionString, [], null, null, objects)
      .InitializeSchemaAsync(cancellationToken);
    await new PostgresSchemaInitializer(_connectionString, [], null, null, objects)
      .InitializeSchemaAsync(cancellationToken);

    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync(cancellationToken);

    var status = await db.ExecuteScalarAsync<string>(
      "SELECT status_description FROM wh_schema_migrations WHERE file_name = 'app:before:fold_label'");

    await Assert.That(status).IsEqualTo("Skipped (hash unchanged)")
      .Because("the second start has to recognize what the first one applied, or every start would "
             + "re-run every object an application owns");
  }
}
