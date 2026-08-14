using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Content tests binding the emitted perspective DDL to the bitemporal column contract: the
/// generator must declare <c>sys_created_at</c> / <c>sys_updated_at</c>, reach EXISTING tables
/// with additive ALTERs, backfill them from their siblings, and index <c>updated_at</c>.
/// </summary>
/// <remarks>
/// The behavioural half lives in <c>BitemporalColumnMigrationTests</c>, which proves the SQL does
/// the right thing when applied. These tests prove the generator actually EMITS it — without
/// them, a correct migration that is never emitted would leave both suites green.
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives</docs>
public class BitemporalPerspectiveSchemaTests {
  private const string SOURCE = """
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record TestEvent : IEvent;

    [PerspectiveStorage(FieldStorageMode.Split)]
    public record BitemporalModel {
      [StreamId]
      public Guid Id { get; init; }

      [PhysicalField]
      public Guid OwnerId { get; init; }
    }

    public class BitemporalPerspective : IPerspectiveFor<BitemporalModel, TestEvent> {
      public BitemporalModel Apply(BitemporalModel currentData, TestEvent @event) => currentData;
    }

    [WhizbangDbContext]
    public class TestDbContext : DbContext {
      public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  private static async Task<string> _generateSchemaAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(SOURCE);
    var schemaExtensions = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("SchemaExtensions"));
    await Assert.That(schemaExtensions).IsNotNull();
    return schemaExtensions!.SourceText.ToString();
  }

  [Test]
  public async Task Generator_DeclaresSystemTimeColumns_OnCreateAsync() {
    var sql = await _generateSchemaAsync();

    await Assert.That(sql).Contains("sys_created_at TIMESTAMPTZ")
      .Because("a new table must carry the operational write-time axis alongside the business one");
    await Assert.That(sql).Contains("sys_updated_at TIMESTAMPTZ");
  }

  [Test]
  public async Task Generator_AddsSystemTimeColumns_ToPreExistingTablesAsync() {
    var sql = await _generateSchemaAsync();

    await Assert.That(sql).Contains("ADD COLUMN IF NOT EXISTS sys_created_at TIMESTAMPTZ")
      .Because("CREATE TABLE IF NOT EXISTS skips a table that already exists, so without an additive "
        + "ALTER the columns appear only for new consumers and are silently missing for every existing one");
    await Assert.That(sql).Contains("ADD COLUMN IF NOT EXISTS sys_updated_at TIMESTAMPTZ");
  }

  [Test]
  public async Task Generator_BackfillsSystemTimeColumns_FromTheirSiblingsAsync() {
    var sql = await _generateSchemaAsync();

    await Assert.That(sql).Contains("sys_created_at = created_at")
      .Because("historical rows must keep the wall-clock write times those columns always held; "
        + "defaulting to NOW() would claim every row was written at upgrade time");
    await Assert.That(sql).Contains("sys_updated_at = updated_at");
    await Assert.That(sql).Contains("WHERE sys_created_at IS NULL")
      .Because("the backfill is guarded so re-running schema init cannot clobber rows whose "
        + "operational stamp has since advanced");
  }

  [Test]
  public async Task Generator_IndexesUpdatedAt_ForTheSlidingReapPredicateAsync() {
    var sql = await _generateSchemaAsync();

    await Assert.That(sql).Contains("(updated_at)")
      .Because("the sliding reap predicate is updated_at < NOW() - interval; unindexed it degrades "
        + "to a sequential scan of every enrolled table on every maintenance cycle");
  }
}
