using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// <c>wh_fold</c> and <c>wh_fold_pattern</c>: the one folding the framework applies to both a stored value and
/// a search term, so a substring search ignores case and typographic variants and can use a trigram index.
/// </summary>
/// <remarks>
/// The same function sits in the index expression and in the query, so term and data can never be folded
/// differently. It must be IMMUTABLE, or PostgreSQL refuses to build the index on it.
/// </remarks>
[Category("Integration")]
public class FoldFunctionTests : PostgresTestBase {

  private async Task<string?> _scalarAsync(string sql, params (string Name, object Value)[] parameters) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    foreach (var (name, value) in parameters) {
      command.Parameters.AddWithValue(name, value);
    }
    var result = await command.ExecuteScalarAsync();
    return result is null or DBNull ? null : result.ToString();
  }

  [Test]
  [Arguments("Nurse Practitioner", "nurse practitioner")]
  [Arguments("O\u2019Brien \u2018Lead\u2019", "o'brien 'lead'")]
  [Arguments("\u201CSenior\u201D \u201EQuote\u201F", "\"senior\" \"quote\"")]
  [Arguments("Level\u2013One\u2014Two\u2212Three\u2010Four\u2011Five\u2012Six\u2015Seven", "level-one-two-three-four-five-six-seven")]
  [Arguments("5\u2032 10\u2033", "5' 10\"")]
  public async Task Fold_LowercasesAndFoldsTypographicVariantsAsync(string input, string expected) {
    await Assert.That(await _scalarAsync("SELECT public.wh_fold(@p)", ("p", input))).IsEqualTo(expected);
  }

  [Test]
  public async Task Fold_NonBreakingSpaces_BecomeOrdinarySpacesAsync() {
    var input = "A" + (char)0x00A0 + "B" + (char)0x202F + "C";

    await Assert.That(await _scalarAsync("SELECT public.wh_fold(@p)", ("p", input))).IsEqualTo("a b c");
  }

  [Test]
  public async Task Fold_OfNull_IsNullAsync() {
    await Assert.That(await _scalarAsync("SELECT public.wh_fold(NULL::text)")).IsNull();
  }

  [Test]
  public async Task Fold_IsImmutable_SoAnIndexCanBeBuiltOnItAsync() {
    await Assert.That(await _scalarAsync(
      "SELECT provolatile FROM pg_proc WHERE proname = 'wh_fold' AND pronamespace = 'public'::regnamespace")).IsEqualTo("i");
    await Assert.That(await _scalarAsync(
      "SELECT provolatile FROM pg_proc WHERE proname = 'wh_fold_pattern' AND pronamespace = 'public'::regnamespace")).IsEqualTo("i");
  }

  [Test]
  public async Task FoldPattern_WrapsTheFoldedTermForAContainsMatch_AndEscapesWildcardsAsync() {
    await Assert.That(await _scalarAsync("SELECT public.wh_fold_pattern(@p)", ("p", "O\u2019Brien"))).IsEqualTo("%o'brien%");
    await Assert.That(await _scalarAsync("SELECT public.wh_fold_pattern(@p)", ("p", "50%_off\\"))).IsEqualTo("%50\\%\\_off\\\\%")
      .Because("a term containing LIKE wildcards matches them literally rather than as wildcards");
  }

  [Test]
  public async Task FoldPattern_MatchesTheStoredValueRegardlessOfCaseAndQuoteStyleAsync() {
    await Assert.That(await _scalarAsync(
      "SELECT public.wh_fold(@stored) LIKE public.wh_fold_pattern(@term)",
      ("stored", "Chief O\u2019Brien \u2013 Operations"), ("term", "o'brien - oper"))).IsEqualTo("True");
    await Assert.That(await _scalarAsync(
      "SELECT public.wh_fold(@stored) LIKE public.wh_fold_pattern(@term)",
      ("stored", "100% Remote"), ("term", "0_ r"))).IsEqualTo("False")
      .Because("an underscore in the term is a literal underscore, not 'any character'");
  }
}
