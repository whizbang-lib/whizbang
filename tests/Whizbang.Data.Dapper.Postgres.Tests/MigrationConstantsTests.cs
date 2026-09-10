using System.Reflection;
using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The migrations' shared literals are defined once (Migrations/constants.txt) and substituted on the same path as
/// __SCHEMA__, so a function body copied forward cannot carry a mistyped copy: every token a migration writes is
/// defined, every migration the provider hands out is free of tokens, a typo is reported by name, and the
/// constants file itself rejects the shapes that would make one substitution eat another.
/// </summary>
/// <docs>operations/infrastructure/migrations#constants</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/MigrationConstants.cs</code-under-test>
public partial class MigrationConstantsTests {
  private static readonly Assembly _assembly = typeof(PostgresMigrationProvider).Assembly;
  private const string MIGRATION_PREFIX = "Whizbang.Data.Postgres.Migrations.";
  private static readonly string[] _theTypo = ["__ENVELOPE_FIELD_MESAGE_ID__"];
  private static readonly string[] _oneTwo = ["__ONE__", "__TWO__"];

  private static IEnumerable<(string Name, string Sql)> _rawMigrations() {
    foreach (var resource in _assembly.GetManifestResourceNames().Where(n => n.StartsWith(MIGRATION_PREFIX, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal)).Order()) {
      using var stream = _assembly.GetManifestResourceStream(resource)!;
      using var reader = new StreamReader(stream);
      yield return (resource[MIGRATION_PREFIX.Length..], reader.ReadToEnd());
    }
  }

  [Test]
  public async Task TheConstantsFile_DefinesEveryTokenTheMigrationsWriteAsync() {
    var undefined = _rawMigrations()
      .Select(m => (m.Name, Unknown: MigrationConstants.UnknownTokens(m.Sql)))
      .Where(x => x.Unknown.Count > 0)
      .Select(x => $"{x.Name}: {string.Join(", ", x.Unknown)}")
      .ToList();

    await Assert.That(undefined).IsEmpty()
      .Because("a token nothing defines would reach the database as an unknown identifier; the file is the one place a literal is spelled");
    await Assert.That(MigrationConstants.Tokens.Count).IsGreaterThanOrEqualTo(8);
  }

  [Test]
  public async Task TheMigrationsTheProviderHandsOut_CarryNoTokens_AndTheValuesAreInPlaceAsync() {
    var provider = new PostgresMigrationProvider(_assembly, "svc");
    var scripts = provider.GetMigrations();

    var leftovers = scripts
      .Select(s => (s.Name, Tokens: _anyToken().Matches(s.Sql).Select(m => m.Value).Distinct().ToList()))
      .Where(x => x.Tokens.Count > 0)
      .Select(x => $"{x.Name}: {string.Join(", ", x.Tokens)}")
      .ToList();
    await Assert.That(leftovers).IsEmpty().Because("the provider substitutes every constant along with the schema");

    var priority = scripts.Single(s => s.Name == "149_MessagePriority");
    await Assert.That(priority.Sql).Contains("elem->>'MessageId'")
      .Because("the store functions read the envelope field by its real name after substitution");
    await Assert.That(priority.Sql).Contains("'00000000-0000-0000-0000-000000000000'::uuid");
    await Assert.That(priority.Sql).Contains("svc.").Because("the schema placeholder is still substituted alongside");
  }

  [Test]
  public async Task Apply_ReplacesTokens_AndLeavesTheSchemaPlaceholderAndPlainTextAloneAsync() {
    const string sql = "SELECT __EMPTY_UUID__::uuid, __CATEGORY_INBOX__ FROM __SCHEMA__.wh_inbox WHERE x = 'literal'";

    var applied = MigrationConstants.Apply(sql);

    await Assert.That(applied).IsEqualTo("SELECT '00000000-0000-0000-0000-000000000000'::uuid, 'inbox' FROM __SCHEMA__.wh_inbox WHERE x = 'literal'");
    await Assert.That(MigrationConstants.Apply("no tokens here")).IsEqualTo("no tokens here");
  }

  [Test]
  public async Task UnknownTokens_NamesATypo_AndIgnoresTheSchemaPlaceholdersAsync() {
    var unknown = MigrationConstants.UnknownTokens(
      "elem->>__ENVELOPE_FIELD_MESAGE_ID__ FROM __SCHEMA__.t, __MIGRATION_SCHEMA__.u WHERE k = __CATEGORY_INBOX__");

    await Assert.That(unknown).IsEquivalentTo(_theTypo)
      .Because("the misspelled token is reported by name, and the schema placeholders and a defined token are not");
  }

  [Test]
  public async Task Parse_ReadsCommentsBlanksAndDefinitions_InOrderAsync() {
    var entries = MigrationConstants.Parse("# a comment\n\n__ONE__ = 'one'\n  __TWO__ = 2 = 2 \n");

    await Assert.That(entries.Select(e => e.Key).ToList()).IsEquivalentTo(_oneTwo);
    await Assert.That(entries[1].Value).IsEqualTo("2 = 2").Because("only the first '=' separates the token from its value");
  }

  [Test]
  public async Task Parse_RejectsTheShapesThatWouldMisfireAsync() {
    await Assert.That(() => MigrationConstants.Parse("__SCHEMA__ = 'x'")).Throws<InvalidDataException>()
      .Because("the schema placeholder is not a constant");
    await Assert.That(() => MigrationConstants.Parse("__lower__ = 'x'")).Throws<InvalidDataException>();
    await Assert.That(() => MigrationConstants.Parse("__A__ = 'x'\n__A__ = 'y'")).Throws<InvalidDataException>()
      .Because("a token defined twice would be ambiguous");
    await Assert.That(() => MigrationConstants.Parse("__A__ =   ")).Throws<InvalidDataException>();
    await Assert.That(() => MigrationConstants.Parse("no separator")).Throws<InvalidDataException>();
    await Assert.That(() => MigrationConstants.Parse("__A__ = 'x'\n__A__B__ = 'y'")).Throws<InvalidDataException>()
      .Because("substituting __A__ first would eat __A__B__");
  }

  [GeneratedRegex("__[A-Z][A-Z0-9_]*__")]
  private static partial Regex _anyToken();
}
