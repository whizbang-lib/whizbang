using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// What the stored-form migration builders accept, without a database: everything they embed in SQL is model
/// metadata, so anything that is not what it claims to be is refused rather than spliced in, and the phase is handed
/// the declarations first and a duplicate name never.
/// </summary>
/// <docs>fundamentals/perspectives/stored-form-migrations</docs>
/// <tests>src/Whizbang.Data.Postgres/StoredFormMigrationSql.cs</tests>
/// <tests>src/Whizbang.Data.Postgres/StoredFormStep.cs</tests>
[Category("Shard1")]
public class StoredFormMigrationSqlTests {
  private static readonly (string Name, string Value)[] _members = [("A", "0"), ("B", "1")];

  [Test]
  public async Task ForPhase_RunsTheMigrationsInTheOrderGivenAsync() {
    var first = StoredFormMigrationSql.Generated("public", "wh_per_a", "one", StoredFormStep.Remove("X"));
    var second = StoredFormMigrationSql.Custom("public", "wh_per_b", new Named("two"));

    var statements = StoredFormMigrationSql.ForPhase("public", [first, second]);

    await Assert.That(string.Join(",", statements.Select(s => s.Name))).IsEqualTo("one,two");
    await Assert.That(statements[0].Name).IsEqualTo("one");
    await Assert.That(statements[1].Sql).IsEqualTo(second.Sql);
    await Assert.That(second.Kind).IsEqualTo(StoredFormMigrationKind.Custom);
    await Assert.That(second.Table).IsEqualTo("wh_per_b");
    await Assert.That(first.Kind).IsEqualTo(StoredFormMigrationKind.Generated);
  }

  [Test]
  public async Task ForPhase_WithNothingDeclared_IsEmpty_AndDeclaringNothingOpensNothingAsync() {
    await Assert.That(StoredFormMigrationSql.ForPhase("public", [])).IsEmpty();
    await StoredFormMigrationSql.DeclareAsync(() => throw new InvalidOperationException("no connection is opened"), "public", []);
  }

  [Test]
  public async Task ForPhase_ADuplicateName_IsRefusedNamingItAsync() {
    var a = StoredFormMigrationSql.Generated("public", "wh_per_a", "same", StoredFormStep.Remove("X"));
    var b = StoredFormMigrationSql.Custom("public", "wh_per_b", new Named("same"));

    var failure = await Assert.That(() => StoredFormMigrationSql.ForPhase("public", [a, b])).Throws<InvalidOperationException>();
    await Assert.That(failure!.Message).Contains("same");
    await Assert.That(() => StoredFormMigrationSql.DeclareAsync(() => throw new InvalidOperationException("unreached"), "public", [a, b]))
      .Throws<InvalidOperationException>().WithMessageContaining("unique");
  }

  [Test]
  public async Task Generated_WithoutSteps_OrWithANullStep_IsRefusedAsync() {
    await Assert.That(() => StoredFormMigrationSql.Generated("public", "t", "n")).Throws<ArgumentException>();
    await Assert.That(() => StoredFormMigrationSql.Generated("public", "t", "n", (StoredFormStep)null!)).Throws<ArgumentException>();
    await Assert.That(() => StoredFormMigrationSql.Generated("public", "t", " ", StoredFormStep.Remove("X"))).Throws<ArgumentException>();
  }

  [Test]
  public async Task Custom_BuildingNoSql_IsRefusedAsync() {
    await Assert.That(() => StoredFormMigrationSql.Custom("public", "t", new Named("n", sql: " "))).Throws<ArgumentException>();
  }

  [Test]
  [Arguments("")]
  [Arguments("Bad Key")]
  [Arguments("A..B")]
  [Arguments("A'; DROP TABLE x; --")]
  public async Task APathThatIsNotAPlainKey_IsRefusedAsync(string path) {
    await Assert.That(() => StoredFormStep.Remove(path)).Throws<ArgumentException>();
  }

  [Test]
  public async Task Members_ThatAreNotPlainNamesAndIntegers_AreRefusedAsync() {
    await Assert.That(() => StoredFormStep.EnumNumberToName("S", [("A B", "0")])).Throws<ArgumentException>();
    await Assert.That(() => StoredFormStep.EnumNumberToName("S", [("A", "1.5")])).Throws<ArgumentException>();
    await Assert.That(() => StoredFormStep.EnumNumberToName("S", [("A", "")])).Throws<ArgumentException>();
    await Assert.That(() => StoredFormStep.ToEnumNumber("S", " ", StoredNumber.Int32, _members, flags: false)).Throws<ArgumentException>();
    await Assert.That(() => StoredFormStep.ToEnumNumber("S", "E", StoredNumber.UInt64, [("Top", "18446744073709551615")], flags: true))
      .Throws<ArgumentException>()
      .Because("A [Flags] combination is computed in bigint.");
  }

  [Test]
  public async Task AnEnumWithoutMembers_StillBuildsAsync() {
    var names = StoredFormMigrationSql.Generated("public", "t", "n", StoredFormStep.ToEnumNumber("S", "E", StoredNumber.Int32, [], flags: false));
    var flags = StoredFormMigrationSql.Generated("public", "t", "n", StoredFormStep.ToEnumNumber("S", "E", StoredNumber.Int32, [], flags: true));

    await Assert.That(names.Sql).Contains("ELSE NULL::numeric END");
    await Assert.That(flags.Sql).Contains("WHERE false");
  }

  [Test]
  public async Task ADefaultThatIsNotJson_IsRefusedAsync() {
    await Assert.That(() => StoredFormStep.DefaultWhenMissing("Tier", "not json")).Throws<ArgumentException>();
  }

  [Test]
  [Arguments("", "TEXT")]
  [Arguments("qty", "")]
  [Arguments("qty", "TEXT; DROP TABLE x")]
  public async Task AColumnOrTypeThatIsNotPlain_IsRefusedAsync(string column, string type) {
    await Assert.That(() => StoredFormStep.RetypeColumn(column, type, number: null)).Throws<ArgumentException>();
  }

  [Test]
  public async Task AColumnRenameBetweenNamesThatAreNotPlain_IsRefusedAsync() {
    await Assert.That(() => StoredFormStep.RenameColumn("a b", "c")).Throws<ArgumentException>();
    await Assert.That(() => StoredFormStep.RenameColumn("a", "c\"")).Throws<ArgumentException>();
    await Assert.That(() => StoredFormStep.Rename("A", "B C")).Throws<ArgumentException>();
  }

  [Test]
  public async Task ANumberKindThatDoesNotExist_IsRefusedAsync() {
    await Assert.That(() => StoredFormStep.ToNumber("N", (StoredNumber)99)).Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task ReplaceIndex_RefusesWhatIsNotAnIndexTheSchemaBuildsOverARootKeyAsync() {
    const string CREATE = "CREATE INDEX IF NOT EXISTS ix ON public.t ((data ->> 'A'));";
    await Assert.That(() => StoredFormStep.ReplaceIndex("A.B", null, "ix", CREATE)).Throws<ArgumentException>()
      .WithMessageContaining("document root");
    await Assert.That(() => StoredFormStep.ReplaceIndex("A", "", "ix", CREATE)).Throws<ArgumentException>();
    await Assert.That(() => StoredFormStep.ReplaceIndex("A", "integer); DROP TABLE t; --", "ix", CREATE)).Throws<ArgumentException>();
    await Assert.That(() => StoredFormStep.ReplaceIndex("A", null, " ", CREATE)).Throws<ArgumentException>();
    await Assert.That(() => StoredFormStep.ReplaceIndex("A", null, "ix", " ")).Throws<ArgumentException>();
    await Assert.That(() => StoredFormStep.ReplaceIndex("A", null, "ix", "DROP INDEX ix")).Throws<ArgumentException>()
      .WithMessageContaining("CREATE [UNIQUE] INDEX IF NOT EXISTS");
    await Assert.That(StoredFormStep.ReplaceIndex("A", "double precision", "ix", CREATE)).IsNotNull();
  }

  [Test]
  public async Task Concurrently_BuildsTheSchemasStatementWithoutBlockingWrites_OrRefusesAnotherStatementAsync() {
    await Assert.That(StoredFormIndexRebuild.Concurrently("CREATE INDEX IF NOT EXISTS ix ON t ((data ->> 'A'));"))
      .IsEqualTo("CREATE INDEX CONCURRENTLY IF NOT EXISTS ix ON t ((data ->> 'A'));");
    await Assert.That(StoredFormIndexRebuild.Concurrently("create unique index if not exists ix ON t (a);"))
      .IsEqualTo("create unique index CONCURRENTLY if not exists ix ON t (a);");
    await Assert.That(StoredFormIndexRebuild.Concurrently("CREATE INDEX ix ON t (a);")).IsNull();
    await Assert.That(StoredFormIndexRebuild.Concurrently("SELECT 1")).IsNull();
  }

  [Test]
  public async Task Generated_CarriesTheIndexesItReplaces_ForTheRebuildAfterThePassAsync() {
    const string CREATE = "CREATE INDEX IF NOT EXISTS ix ON public.t ((data ->> 'A'));";

    var migration = StoredFormMigrationSql.Generated("public", "wh_per_t", "m",
      StoredFormStep.ReplaceIndex("A", null, "ix", CREATE), StoredFormStep.ToText("A"));

    await Assert.That(migration.IndexRebuilds).IsEquivalentTo([new StoredFormIndexRebuild("public", "wh_per_t", "ix", CREATE)]);
    await Assert.That(StoredFormMigrationSql.Custom("public", "wh_per_t", new Named("c")).IndexRebuilds).IsEmpty();
  }

  [Test]
  public async Task RetryParkedStreams_NeedsAtLeastOneNamedPerspectiveAsync() {
    await Assert.That(() => StoredFormStep.RetryParkedStreams()).Throws<ArgumentException>();
    await Assert.That(() => StoredFormStep.RetryParkedStreams("App.A", " ")).Throws<ArgumentException>();
    await Assert.That(() => StoredFormStep.RetryParkedStreams(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => StoredFormMigrationSql.Custom("public", "t", new Named("c"), null!)).Throws<ArgumentNullException>();
  }

  private sealed class Named(string name, string sql = "SELECT 1") : IStoredFormMigration {
    public string Name => name;

    public string BuildSql(StoredFormMigrationTarget target) => sql;
  }
}
