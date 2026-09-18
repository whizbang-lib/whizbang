using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Locks the quoting of a PostgreSQL identifier interpolated into SQL text.
/// </summary>
/// <remarks>
/// <para>
/// A value is always bound as a parameter, but an identifier cannot be — <c>SELECT @p()</c> is not
/// a function call — so every schema-qualified statement interpolates its schema name. That
/// interpolation was written independently in ten places as <c>$"\"{schema}\".{name}"</c>, and none
/// escaped a quote inside the schema. PostgreSQL ends a quoted identifier at the first unescaped
/// quote and parses what follows as SQL, so a schema carrying one closes the identifier early.
/// </para>
/// <para>
/// The schema normally comes from the EF Core model and is a developer constant, which is what the
/// ten suppressions at those call sites asserted. Per-schema multi-tenancy is a supported
/// deployment, and there the schema is chosen per tenant, so "not user input" is a property of the
/// caller rather than of the helper. These tests pin the helper's own guarantee instead.
/// </para>
/// </remarks>
[Category("Unit")]
public class PgIdentifierTests {

  [Test]
  public async Task Quote_DoublesAnEmbeddedQuote_SoItCannotEndTheIdentifierAsync() {
    // The injection shape: an unescaped quote would close the identifier and leave "; DROP ..." as SQL.
    var quoted = PgIdentifier.Quote("evil\"; DROP TABLE wh_outbox; --");

    await Assert.That(quoted).IsEqualTo("\"evil\"\"; DROP TABLE wh_outbox; --\"")
      .Because("the quote must be doubled, which keeps the whole string inside one identifier");
    // Exactly two delimiters: the opening and closing ones. Everything between is escaped.
    await Assert.That(quoted.StartsWith('"') && quoted.EndsWith('"')).IsTrue();
  }

  [Test]
  public async Task Quote_LeavesAnOrdinaryNameIntactAsync()
    => await Assert.That(PgIdentifier.Quote("tenant_42")).IsEqualTo("\"tenant_42\"");

  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments("   ")]
  [Arguments("public")]
  public async Task QualifyPrefix_IsEmptyWhereNoSchemaAppliesAsync(string? schema)
    => await Assert.That(PgIdentifier.QualifyPrefix(schema)).IsEqualTo(string.Empty)
      .Because("a prefix for the default schema would emit a leading dot and break the statement");

  [Test]
  public async Task QualifyPrefix_QuotesANonDefaultSchemaAsync()
    => await Assert.That(PgIdentifier.QualifyPrefix("reporting")).IsEqualTo("\"reporting\".");

  [Test]
  public async Task Qualify_ComposesPrefixAndNameAsync() {
    await Assert.That(PgIdentifier.Qualify("reporting", "claim_work")).IsEqualTo("\"reporting\".claim_work");
    await Assert.That(PgIdentifier.Qualify("public", "claim_work")).IsEqualTo("claim_work");
    await Assert.That(PgIdentifier.Qualify(null, "claim_work")).IsEqualTo("claim_work");
  }

  [Test]
  [Arguments("wh_perspective_rows")]
  [Arguments("Tenant42")]
  [Arguments("_leading_underscore")]
  [Arguments("a")]
  public async Task RequireBare_ReturnsABareIdentifierUnchangedAsync(string identifier)
    => await Assert.That(PgIdentifier.RequireBare(identifier, "tableName")).IsEqualTo(identifier);

  [Test]
  [Arguments("evil\"; DROP TABLE wh_outbox; --")]
  [Arguments("has space")]
  [Arguments("has-dash")]
  [Arguments("has.dot")]
  [Arguments("semi;colon")]
  [Arguments("paren()")]
  public async Task RequireBare_ThrowsOnAnythingThatIsNotLetterDigitOrUnderscoreAsync(string identifier)
    => await Assert.That(() => PgIdentifier.RequireBare(identifier, "tableName"))
      .Throws<ArgumentException>()
      .Because("this identifier reaches SQL unquoted, so anything outside the bare set could change the statement");

  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments("   ")]
  public async Task RequireBare_ThrowsOnAnAbsentIdentifierAsync(string? identifier)
    => await Assert.That(() => PgIdentifier.RequireBare(identifier!, "tableName")).Throws<ArgumentException>();

  [Test]
  public async Task RequireBare_NamesTheOffendingParameterAsync() {
    var ex = await Assert.That(() => PgIdentifier.RequireBare("bad name", "tableName"))
      .Throws<ArgumentException>();
    await Assert.That(ex!.ParamName).IsEqualTo("tableName")
      .Because("the caller's parameter name is what makes the failure actionable, not the helper's");
  }

  [Test]
  public async Task Qualify_EscapesTheSchemaItQualifiesWithAsync()
    => await Assert.That(PgIdentifier.Qualify("a\"b", "claim_work")).IsEqualTo("\"a\"\"b\".claim_work")
      .Because("the qualified form must escape exactly as the bare quoted form does");
}
