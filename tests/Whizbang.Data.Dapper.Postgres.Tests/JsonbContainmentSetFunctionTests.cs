using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The helper migration 152 adds, which turns an array of candidate values into the array of
/// single-key documents a set-membership containment filter consumes.
/// </summary>
/// <remarks>
/// <para>
/// A filter of the form "this field is any of these values" only reaches the GIN index on the data
/// column as <c>data @&gt; ANY(...)</c>, and containment compares a document with a document, so the
/// right-hand side has to be one document per candidate. The values arrive as a single array
/// parameter, and turning that into an array of documents inline needs a scalar subquery the query
/// translator cannot build. A function call it can.
/// </para>
/// <para>
/// Two properties matter and both are asserted: the documents come out right, and the function stays
/// inlinable so the planner still matches the index. An opaque call would be correct and would plan
/// as a sequential scan, which would defeat the entire point.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/152_JsonbContainmentSet.sql</code-under-test>
[Category("Integration")]
public class JsonbContainmentSetFunctionTests : PostgresTestBase {
  private static readonly string[] _candidates = ["a", "b"];

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

  /// <summary>One document per value, keyed by the name given.</summary>
  [Test]
  public async Task BuildsOneDocumentPerValueAsync() {
    var first = await _scalarAsync(
      "SELECT (public.jsonb_containment_set('Title', @p))[1]::text",
      ("p", _candidates));
    var second = await _scalarAsync(
      "SELECT (public.jsonb_containment_set('Title', @p))[2]::text",
      ("p", _candidates));

    await Assert.That(first).IsEqualTo("{\"Title\": \"a\"}");
    await Assert.That(second).IsEqualTo("{\"Title\": \"b\"}");
  }

  /// <summary>
  /// It is polymorphic over the element type, which matters because the values a repository filters
  /// by are as often identifiers as they are strings.
  /// </summary>
  [Test]
  public async Task WorksForIdentifierValuesAsync() {
    var id = Guid.NewGuid();
    var text = await _scalarAsync(
      "SELECT public.jsonb_containment_set('TenantId', @p)::text",
      ("p", new[] { id }));

    await Assert.That(text).IsNotNull();
    await Assert.That(text).Contains(id.ToString(), StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>
  /// An empty candidate set yields NULL, and a containment test against NULL matches nothing. That is
  /// the right answer: asking for rows whose field is any of no values should return none.
  /// </summary>
  [Test]
  public async Task AnEmptySetMatchesNothingAsync() {
    var built = await _scalarAsync(
      "SELECT public.jsonb_containment_set('Title', @p)::text",
      ("p", Array.Empty<string>()));

    await Assert.That(built).IsNull();

    var matched = await _scalarAsync(
      "SELECT (('{\"Title\":\"a\"}'::jsonb) @> ANY(public.jsonb_containment_set('Title', @p)))::text",
      ("p", Array.Empty<string>()));

    await Assert.That(matched).IsNull();
  }

  /// <summary>A NULL array short-circuits rather than reaching the body, because the function is STRICT.</summary>
  [Test]
  public async Task ANullSetMatchesNothingAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(
      "SELECT public.jsonb_containment_set('Title', NULL::text[])::text", connection);

    var result = await command.ExecuteScalarAsync();

    await Assert.That(result is null or DBNull).IsTrue();
  }

  /// <summary>
  /// Membership agrees with a chain of equality comparisons, which is the semantics the rewrite
  /// claims to preserve.
  /// </summary>
  [Test]
  public async Task MembershipAgreesWithEqualityAsync() {
    var matched = await _scalarAsync(
      "SELECT (('{\"Title\":\"b\"}'::jsonb) @> ANY(public.jsonb_containment_set('Title', @p)))::text",
      ("p", _candidates));

    await Assert.That(matched).IsEqualTo("true");

    var missed = await _scalarAsync(
      "SELECT (('{\"Title\":\"z\"}'::jsonb) @> ANY(public.jsonb_containment_set('Title', @p)))::text",
      ("p", _candidates));

    await Assert.That(missed).IsEqualTo("false");
  }

  /// <summary>
  /// Declared IMMUTABLE, which is what lets the planner inline it and still match the index. If this
  /// ever reads STABLE or VOLATILE the set-membership rewrite silently becomes a sequential scan.
  /// </summary>
  [Test]
  public async Task IsImmutableSoThePlannerCanInlineItAsync() {
    var volatility = await _scalarAsync(
      "SELECT provolatile::text FROM pg_proc WHERE proname = 'jsonb_containment_set'");

    await Assert.That(volatility).IsEqualTo("i");
  }
}
