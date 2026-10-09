// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Data.EFCore.Postgres.Dispatch;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres.Schema;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The maintenance step reconciles between starts, so a drop a start held back lands without another deploy;
/// once per fleet per claim window, and only for a context that registered its manifest.
/// </summary>
/// <docs>fundamentals/perspectives/managed-schema-objects#every-later-start</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class ManagedSchemaReconcileStepTests {
  private const string TABLE = "wh_per_document_index_opted_out";
  private const string RETIRED = "idx_document_index_opted_out_data_gin";
  private static readonly DateTimeOffset _now = new(2026, 10, 9, 12, 5, 0, TimeSpan.Zero);

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("mgd_step");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
    await using var context = _context();
    await context.EnsureWhizbangDatabaseInitializedAsync();
    // What an earlier release left, recorded as pending retirement by the start after it appeared.
    await _execAsync($"CREATE INDEX {RETIRED} ON {TABLE} USING gin (data)");
    await context.EnsureWhizbangDatabaseInitializedAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  private DocumentIndexesDbContext _context() => new(
    new DbContextOptionsBuilder<DocumentIndexesDbContext>()
      .UseNpgsql(_connectionString)
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);

  private async Task _execAsync(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<bool> _existsAsync(string index) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand("SELECT to_regclass('public.' || $1) IS NOT NULL", db);
    command.Parameters.AddWithValue(index);
    return await command.ExecuteScalarAsync() is true;
  }

  private async Task _runAsync(bool withManifest = true, bool withClaimStore = true, params (string Key, string Value)[] config) {
    await using var context = _context();
    var services = new ServiceCollection();
    services.AddSingleton(context);
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
      .AddInMemoryCollection(config.Select(c => new KeyValuePair<string, string?>(c.Key, c.Value))).Build());
    if (withManifest) {
      services.AddKeyedSingleton<ManagedSchemaManifest>(typeof(DocumentIndexesDbContext), new ManagedSchemaManifest(
        typeof(DocumentIndexesDbContext), "public", DocumentIndexesDbContextSchemaExtensions.GetManagedSchemaObjects));
    }
    if (withClaimStore) {
      services.AddSingleton<IClaimedEmissionStore>(new EFCoreClaimedEmissionStore(context));
    }
    await using var provider = services.BuildServiceProvider();
    await new ManagedSchemaReconcileStep(typeof(DocumentIndexesDbContext), timeProvider: new FakeTimeProvider(_now))
      .RunAsync(provider, CancellationToken.None);
  }

  [Test]
  public async Task ARun_DropsWhatAStartHeldBackAsync() {
    await _runAsync();

    await Assert.That(await _existsAsync(RETIRED)).IsFalse();
  }

  [Test]
  public async Task ASecondRunInTheSameWindow_ReconcilesNothingAsync() {
    await _execAsync($"SELECT public.wh_pin_object('{TABLE}', '{RETIRED}', 'held for the first run')");
    await _runAsync();
    await _execAsync($"SELECT public.wh_unpin_object('{TABLE}', '{RETIRED}')");

    await _runAsync();

    await Assert.That(await _existsAsync(RETIRED)).IsTrue()
      .Because("the window's claim is taken, so this run belongs to an instance that stands down");
  }

  [Test]
  public async Task WithoutAClaimStore_TheStepStandsDownAsync() {
    await _runAsync(withClaimStore: false);

    await Assert.That(await _existsAsync(RETIRED)).IsTrue();
  }

  [Test]
  public async Task AContextWithNoManifest_IsLeftAloneAsync() {
    await _runAsync(withManifest: false);

    await Assert.That(await _existsAsync(RETIRED)).IsTrue();
  }

  [Test]
  public async Task WithTheReconcileOff_TheStepDoesNothingAsync() {
    await _runAsync(config: ("Whizbang:Schema:Reconcile:Mode", "Off"));

    await Assert.That(await _existsAsync(RETIRED)).IsTrue();
  }
}
