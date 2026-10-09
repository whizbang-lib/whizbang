// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres.Schema;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The guard on the managed-object manifest: after the shipped schema pass builds a context's perspectives,
/// every object on their tables is one the generated manifest declares, and every declared object exists.
/// </summary>
/// <remarks>
/// The reconcile drops a Whizbang-shaped object the manifest does not declare. An object the schema pass
/// builds and the manifest misses would be dropped on the next start and built again on the one after, so a
/// gap here is a defect in the manifest, never in this test. The sync triggers of a physical-field move are
/// owned by the move, which drops them when it settles, and are left out.
/// </remarks>
/// <docs>fundamentals/perspectives/managed-schema-objects</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class ManagedObjectsDeclaredTests {
  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("mgd_declared");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  private DbContextOptions<TContext> _options<TContext>() where TContext : DbContext =>
    new DbContextOptionsBuilder<TContext>()
      .UseNpgsql(_connectionString)
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;

  [Test]
  [Timeout(180000)]
  public async Task DocumentIndexes_EveryObjectBuiltIsDeclaredAsync(CancellationToken cancellationToken) {
    await using var context = new DocumentIndexesDbContext(_options<DocumentIndexesDbContext>());
    await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);

    await _assertDeclaredAsync(DocumentIndexesDbContextSchemaExtensions.GetManagedSchemaObjects(), cancellationToken);
  }

  [Test]
  [Timeout(180000)]
  public async Task PhysicalPromotion_EveryObjectBuiltIsDeclaredAsync(CancellationToken cancellationToken) {
    await using var context = new PhysicalPromotionDbContext(_options<PhysicalPromotionDbContext>());
    await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);

    await _assertDeclaredAsync(PhysicalPromotionDbContextSchemaExtensions.GetManagedSchemaObjects(), cancellationToken);
  }

  [Test]
  [Timeout(180000)]
  public async Task JsonbColumns_EveryObjectBuiltIsDeclaredAsync(CancellationToken cancellationToken) {
    await using var context = new JsonbColumnsDbContext(_options<JsonbColumnsDbContext>());
    await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);

    await _assertDeclaredAsync(JsonbColumnsDbContextSchemaExtensions.GetManagedSchemaObjects(), cancellationToken);
  }

  [Test]
  [Timeout(180000)]
  public async Task PhysicalMoves_EveryObjectBuiltIsDeclaredAsync(CancellationToken cancellationToken) {
    await using var context = new PhysicalMovesDbContext(_options<PhysicalMovesDbContext>());
    await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);

    await _assertDeclaredAsync(PhysicalMovesDbContextSchemaExtensions.GetManagedSchemaObjects(), cancellationToken);
  }

  [Test]
  [Timeout(300000)]
  public async Task WorkCoordination_EveryObjectBuiltIsDeclaredAsync(CancellationToken cancellationToken) {
    await using var context = new WorkCoordinationDbContext(_options<WorkCoordinationDbContext>());
    await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);

    await _assertDeclaredAsync(WorkCoordinationDbContextSchemaExtensions.GetManagedSchemaObjects(), cancellationToken);
  }

  private async Task _assertDeclaredAsync(ManagedSchemaObjectSet declared, CancellationToken cancellationToken) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    var live = await ManagedSchemaCatalog.ReadLiveAsync(connection, "public", cancellationToken);
    var declaredNames = declared.Objects.Select(o => $"{o.Table}:{o.Name}").ToHashSet(StringComparer.Ordinal);
    var liveNames = live
      .Where(o => !o.Name.StartsWith("wh_mv_", StringComparison.Ordinal))
      .Select(o => $"{o.Table}:{o.Name}")
      .ToHashSet(StringComparer.Ordinal);

    await Assert.That(declared.Objects).IsNotEmpty();
    await Assert.That(liveNames.Except(declaredNames).Order(StringComparer.Ordinal).ToList()).IsEmpty()
      .Because("an object the schema pass builds and the manifest misses is dropped on the next start");
    await Assert.That(declaredNames.Except(liveNames).Order(StringComparer.Ordinal).ToList()).IsEmpty()
      .Because("an object the manifest declares and the schema pass never builds is reported missing at every start");
  }
}
