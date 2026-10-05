// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Where the row-version guard reads a perspective's version from (issue #928): the table the context maps
/// the perspective to, schema-qualified and quoted, and a clear failure when there is none. No database.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/PerspectiveRowVersionSql.cs</code-under-test>
[Category("Unit")]
[Category("Shard1")]
public class PerspectiveRowVersionSqlTests {
  private sealed class Model {
    public string Name { get; set; } = string.Empty;
  }

  // One context type per mapping: EF caches a model per context type, so a mapping cannot vary per instance.
  private abstract class MappedContext(DbContextOptions options) : DbContext(options) {
    protected abstract void Map(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<PerspectiveRow<Model>> entity);

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<Model>>(entity => {
        Map(entity);
        entity.HasKey(e => e.Id);
        entity.Ignore(e => e.Data);
        entity.Ignore(e => e.Metadata);
        entity.Ignore(e => e.Scope);
      });
    }
  }

  private sealed class DefaultSchemaContext(DbContextOptions<DefaultSchemaContext> options) : MappedContext(options) {
    protected override void Map(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<PerspectiveRow<Model>> entity) =>
      entity.ToTable("wh_per_model");
  }

  private sealed class ServiceSchemaContext(DbContextOptions<ServiceSchemaContext> options) : MappedContext(options) {
    protected override void Map(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<PerspectiveRow<Model>> entity) =>
      entity.ToTable("wh_per_model", "inventory");
  }

  private sealed class ViewContext(DbContextOptions<ViewContext> options) : MappedContext(options) {
    protected override void Map(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<PerspectiveRow<Model>> entity) =>
      entity.ToView("v_model");
  }

  private sealed class EmptyContext(DbContextOptions<EmptyContext> options) : DbContext(options) { }

  private static DbContextOptions<T> _npgsql<T>() where T : DbContext =>
    new DbContextOptionsBuilder<T>().UseNpgsql("Host=localhost;Database=unused").Options;

  [Test]
  public async Task QualifiedTable_InTheDefaultSchema_IsTheQuotedTableAsync() {
    await using var context = new DefaultSchemaContext(_npgsql<DefaultSchemaContext>());

    await Assert.That(PerspectiveRowVersionSql.QualifiedTable<Model>(context)).IsEqualTo("\"wh_per_model\"");
  }

  [Test]
  public async Task QualifiedTable_InAServiceSchema_IsSchemaQualifiedAsync() {
    await using var context = new ServiceSchemaContext(_npgsql<ServiceSchemaContext>());

    await Assert.That(PerspectiveRowVersionSql.QualifiedTable<Model>(context)).IsEqualTo("\"inventory\".\"wh_per_model\"")
      .Because("the version must be read from the table the model is read from, not whatever search_path resolves");
  }

  [Test]
  public async Task QualifiedTable_WhenThePerspectiveIsNotMapped_ThrowsAsync() {
    await using var context = new EmptyContext(_npgsql<EmptyContext>());

    await Assert.That(() => PerspectiveRowVersionSql.QualifiedTable<Model>(context))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task QualifiedTable_WhenThePerspectiveIsMappedToAView_ThrowsAsync() {
    await using var context = new ViewContext(_npgsql<ViewContext>());

    await Assert.That(() => PerspectiveRowVersionSql.QualifiedTable<Model>(context))
      .Throws<InvalidOperationException>()
      .Because("a view has no row versions to guard a write with");
  }

  [Test]
  public async Task Supports_OnlyThePostgresProviderAsync() {
    await using var npgsql = new EmptyContext(_npgsql<EmptyContext>());
    await using var inMemory = new EmptyContext(
      new DbContextOptionsBuilder<EmptyContext>().UseInMemoryDatabase($"supports-{Guid.NewGuid():N}").Options);

    await Assert.That(PerspectiveRowVersionSql.Supports(npgsql)).IsTrue();
    await Assert.That(PerspectiveRowVersionSql.Supports(inMemory)).IsFalse();
  }

  [Test]
  public async Task ExpectedVersionParameter_BindsTheUnsignedTransactionIdAsync() {
    var parameter = PerspectiveRowVersionSql.ExpectedVersionParameter("p", PerspectiveRowVersion.Of(4_000_000_000L));

    await Assert.That(parameter.NpgsqlDbType).IsEqualTo(NpgsqlTypes.NpgsqlDbType.Xid);
    await Assert.That(parameter.Value).IsEqualTo((object)4_000_000_000u);
  }
}
