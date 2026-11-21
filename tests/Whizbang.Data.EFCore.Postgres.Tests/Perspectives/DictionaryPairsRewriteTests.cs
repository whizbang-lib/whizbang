// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Functions;
using Whizbang.Data.EFCore.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Which dictionaries the stored-form rewrite finds, how it rebuilds each key kind, and the statement's guards, without
/// a database (#1911).
/// </summary>
/// <docs>fundamentals/perspectives/dictionary-members#stored-form-rewrite</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/DictionaryPairsRewrite.cs</code-under-test>
[Category("Shard2")]
public class DictionaryPairsRewriteTests {
  private const string OFFLINE = "Host=localhost;Database=probe;Username=u;Password=p";
  private static readonly string[] _tableOrder = ["wh_per_dictionary_another", "wh_per_dictionary_opaque"];

  /// <summary>A model stored as one value, with a dictionary in it and one in a promoted jsonb column.</summary>
  private sealed class OpaqueDbContext(DbContextOptions<OpaqueDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      // A second table, named ahead of the first, so the rewrites come back in table order whatever order the model holds.
      modelBuilder.Entity<PerspectiveRow<DictionaryMemberModel>>(entity => {
        entity.ToTable("wh_per_dictionary_another");
        entity.HasKey(e => e.Id);
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
        entity.Ignore(e => e.Metadata);
        entity.Ignore(e => e.Scope);
      });
      modelBuilder.Entity<PerspectiveRow<DictionaryRewriteModel>>(entity => {
        entity.ToTable("wh_per_dictionary_opaque");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Data).HasColumnName("data").HasColumnType("jsonb")
          .HasConversion(PerspectiveDocumentSerialization.ConverterFor<DictionaryRewriteModel>());
        entity.Property<Dictionary<string, int>>("by_name").HasColumnName("by_name").HasColumnType("jsonb")
          .HasConversion(PerspectiveDocumentSerialization.ConverterFor<Dictionary<string, int>>());
        // A jsonb column of a type no serializer metadata describes: nothing to walk.
        entity.Property<Unregistered>("unregistered").HasColumnName("unregistered").HasColumnType("jsonb")
          .HasConversion(v => "{}", v => new Unregistered());
        // A complex property spread over columns rather than a document: not a place a dictionary is stored.
        entity.ComplexProperty(e => e.Metadata);
        entity.Ignore(e => e.Scope);
      });
    }
  }

  /// <summary>A type no serializer metadata describes.</summary>
  private sealed class Unregistered;

  /// <summary>A generic entity that is not a perspective row.</summary>
  public class GenericHolder<T> {
    public Guid Id { get; set; }
    public T? Value { get; set; }
  }

  private sealed class NotAPerspectiveDbContext(DbContextOptions<NotAPerspectiveDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<DictionaryMemberLine>(entity => {
        entity.ToTable("lines");
        entity.HasKey(e => e.Name);
      });
      modelBuilder.Entity<GenericHolder<int>>(entity => entity.ToTable("holders"));
      // A perspective row whose model holds no dictionary.
      modelBuilder.Entity<PerspectiveRow<DictionaryMemberLine>>(entity => {
        entity.ToTable("wh_per_lines");
        entity.HasKey(e => e.Id);
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
        entity.Ignore(e => e.Metadata);
        entity.Ignore(e => e.Scope);
      });
      // A perspective row mapped to no table.
      modelBuilder.Entity<PerspectiveRow<DictionaryMemberSection>>(entity => {
        entity.ToTable((string?)null);
        entity.HasKey(e => e.Id);
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
        entity.Ignore(e => e.Metadata);
        entity.Ignore(e => e.Scope);
      });
    }
  }

  private static TContext _context<TContext>(Func<DbContextOptions<TContext>, TContext> make) where TContext : DbContext =>
    make(new DbContextOptionsBuilder<TContext>()
      .UseNpgsql(OFFLINE, npgsql => npgsql.UseWhizbangFunctions())
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);

  [Test]
  public async Task AStoredAsOneValueDocumentAndAPromotedColumn_AreWalkedThroughTheSerializerAsync() {
    await using var context = _context<OpaqueDbContext>(o => new OpaqueDbContext(o));

    var tables = DictionaryPairsRewrite.ForModel(context.Model, PerspectiveDocumentSerialization.Options, "public");
    var paths = DictionaryPairsRewrite.PathsOf(
      context.Model.FindEntityType(typeof(PerspectiveRow<DictionaryRewriteModel>))!, PerspectiveDocumentSerialization.Options);

    await Assert.That(tables.Select(t => t.Table)).IsEquivalentTo(_tableOrder, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(paths.Select(p => p.ToString())).Contains("data:ByName");
    await Assert.That(paths.Select(p => p.ToString())).Contains("data:Sections.[].Totals");
    await Assert.That(paths.Select(p => p.ToString())).Contains("data:Nested.[].Value");
    await Assert.That(paths.Select(p => p.ToString())).Contains("by_name:");
    await Assert.That(paths.Select(p => p.ToString())).Contains("data:Root.Children")
      .Because("a dictionary whose value holds the type the walk is already inside is found, and the walk ends there");
    await Assert.That(paths.Any(p => p.Column is "unregistered" or "metadata")).IsFalse();
  }

  [Test]
  public async Task ARowThatIsNoPerspectiveOrHoldsNoDictionaryOrHasNoTable_HasNoRewriteAsync() {
    await using var context = _context<NotAPerspectiveDbContext>(o => new NotAPerspectiveDbContext(o));

    await Assert.That(DictionaryPairsRewrite.ForModel(context.Model, PerspectiveDocumentSerialization.Options, "public")).IsEmpty();
  }

  [Test]
  public async Task TheJournalName_ChangesWithThePathsAsync() {
    await using var context = _context<OpaqueDbContext>(o => new OpaqueDbContext(o));
    var paths = DictionaryPairsRewrite.PathsOf(
      context.Model.FindEntityType(typeof(PerspectiveRow<DictionaryRewriteModel>))!, PerspectiveDocumentSerialization.Options);

    var all = DictionaryPairsRewrite.JournalName("wh_per_x", paths);
    var fewer = DictionaryPairsRewrite.JournalName("wh_per_x", paths.RemoveAt(0));

    await Assert.That(all).StartsWith("dictionary-pairs:wh_per_x:");
    await Assert.That(fewer).IsNotEqualTo(all).Because("a model that gains a dictionary is scanned again");
  }

  [Test]
  public async Task EachKeyType_HasTheKindTheDatabaseRebuildsItWithAsync() {
    var options = PerspectiveDocumentSerialization.Options;

    await Assert.That(DictionaryPairsRewrite.KeyFormOf(DictionaryMemberForm<string, int>.Instance, options)!.Value.Kind).IsEqualTo((short)1);
    await Assert.That(DictionaryPairsRewrite.KeyFormOf(DictionaryMemberForm<int, string>.Instance, options)!.Value.Kind).IsEqualTo((short)2);
    var color = DictionaryPairsRewrite.KeyFormOf(DictionaryMemberForm<DictionaryRewriteColor, int>.Instance, options)!.Value;
    await Assert.That(color.Kind).IsEqualTo((short)3);
    await Assert.That(color.Map).IsEqualTo("""{"Red":1,"Green":2}""");
    await Assert.That(DictionaryPairsRewrite.KeyFormOf(DictionaryMemberForm<DateTime, int>.Instance, options)).IsNull();
  }

  [Test]
  public async Task TheStatement_SkipsAKeyTheDatabaseCannotRebuild_AndRecordsItsJournalAsync() {
    await using var context = _context<OpaqueDbContext>(o => new OpaqueDbContext(o));
    var paths = DictionaryPairsRewrite.PathsOf(
      context.Model.FindEntityType(typeof(PerspectiveRow<DictionaryRewriteModel>))!, PerspectiveDocumentSerialization.Options);

    var sql = DictionaryPairsRewrite.StatementFor("svc", "wh_per_x", paths, PerspectiveDocumentSerialization.Options);

    await Assert.That(sql).Contains("\"svc\".wh_dictionary_pairs_at(\"data\", ARRAY['ByName']::text[], 1::smallint, NULL::jsonb)");
    await Assert.That(sql).DoesNotContain("'ByDay'");
    await Assert.That(sql).Contains("\"svc\".wh_stored_form_migrations");
  }

  [Test]
  public async Task NullArguments_AreRefusedAsync() {
    await using var context = _context<OpaqueDbContext>(o => new OpaqueDbContext(o));
    var options = PerspectiveDocumentSerialization.Options;
    var rowType = context.Model.FindEntityType(typeof(PerspectiveRow<DictionaryRewriteModel>))!;

    await Assert.That(() => DictionaryPairsRewrite.ForModel(null!, options, "public")).Throws<ArgumentNullException>();
    await Assert.That(() => DictionaryPairsRewrite.ForModel(context.Model, null!, "public")).Throws<ArgumentNullException>();
    await Assert.That(() => DictionaryPairsRewrite.ForModel(context.Model, options, null!)).Throws<ArgumentNullException>();
    await Assert.That(() => DictionaryPairsRewrite.PathsOf(null!, options)).Throws<ArgumentNullException>();
    await Assert.That(() => DictionaryPairsRewrite.PathsOf(rowType, null!)).Throws<ArgumentNullException>();
    await Assert.That(() => DictionaryPairsRewrite.StatementFor(null!, "t", [], options)).Throws<ArgumentNullException>();
    await Assert.That(() => DictionaryPairsRewrite.StatementFor("s", null!, [], options)).Throws<ArgumentNullException>();
    await Assert.That(() => DictionaryPairsRewrite.StatementFor("s", "t", [], null!)).Throws<ArgumentNullException>();
  }
}
