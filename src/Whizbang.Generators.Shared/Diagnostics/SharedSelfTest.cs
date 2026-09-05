using System.Collections.Generic;
using Whizbang.Generators.Shared.Limits;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Shared.Diagnostics;

/// <summary>
/// Exercises the parts of the shared surface that cannot be driven from outside a merged copy.
/// </summary>
/// <remarks>
/// <para>ILRepack merges this assembly into every generator package, so each package carries its
/// own copy of these types. A test in another assembly can reach the copies by reflection, which
/// covers most of the surface -- but not members whose parameters are themselves merged types.
/// <see cref="IdentifierValidation"/> is the case in point: its methods take an
/// <see cref="IDbProviderLimits"/>, and each merged copy has a distinct type identity for that
/// interface, so no single class declared in a test assembly can satisfy all of them.</para>
///
/// <para>The way out is for the check to live here. This class and the limits implementation
/// below are merged into every host alongside the code they exercise, so each copy holds an
/// implementation whose identity already matches its own copy of the interface. Each package's
/// test then calls <see cref="Run"/> on that package's copy and asserts the result is empty.</para>
///
/// <para>It is public because that is what makes it callable from the merged copies, where the
/// types are internal in three of the four hosts.</para>
/// </remarks>
public static class SharedSelfTest {

  /// <summary>Provider limits used only by <see cref="Run"/>. Merged into every host with it.</summary>
  private sealed class SelfTestLimits : IDbProviderLimits {
    public int MaxTableNameBytes => 10;
    public int MaxColumnNameBytes => 10;
    public int MaxIndexNameBytes => 10;
    public string ProviderName => "SelfTest";
  }

  /// <summary>
  /// Runs every check. Returns an empty list when the copy behaves correctly, otherwise one
  /// message per failure so a caller reports what diverged rather than only that something did.
  /// </summary>
  public static IReadOnlyList<string> Run() {
    var failures = new List<string>();
    var limits = new SelfTestLimits();

    // Byte count, not character count: a name inside the character limit can still exceed the
    // byte limit, and PostgreSQL's NAMEDATALEN is counted in bytes. Getting this wrong truncates
    // identifiers in the database while the generator reports success.
    _expect(failures, IdentifierValidation.GetByteCount("abc") == 3,
      "GetByteCount: ASCII is one byte per character");
    _expect(failures, IdentifierValidation.GetByteCount("\u00e9") == 2,
      "GetByteCount: a two-byte character must count as two, not one");
    _expect(failures, IdentifierValidation.GetByteCount("") == 0,
      "GetByteCount: an empty identifier is zero bytes");

    // Each validator answers null when the name fits and a message naming the provider when it
    // does not. A copy that answered the other way round would either reject every valid name or
    // silently accept names the database will refuse.
    _expectValid(failures, "table", IdentifierValidation.ValidateTableName("orders", limits));
    _expectValid(failures, "column", IdentifierValidation.ValidateColumnName("id", limits));
    _expectValid(failures, "index", IdentifierValidation.ValidateIndexName("ix_id", limits));

    _expectRejected(failures, "table",
      IdentifierValidation.ValidateTableName("a_very_long_table_name", limits));
    _expectRejected(failures, "column",
      IdentifierValidation.ValidateColumnName("a_very_long_column_name", limits));
    _expectRejected(failures, "index",
      IdentifierValidation.ValidateIndexName("a_very_long_index_name", limits));

    // The boolean companions must agree with the validators they wrap.
    _expect(failures, IdentifierValidation.IsTableNameValid("orders", limits),
      "IsTableNameValid disagreed with ValidateTableName on a valid name");
    _expect(failures, !IdentifierValidation.IsTableNameValid("a_very_long_table_name", limits),
      "IsTableNameValid disagreed with ValidateTableName on an oversized name");
    _expect(failures, IdentifierValidation.IsColumnNameValid("id", limits),
      "IsColumnNameValid disagreed with ValidateColumnName on a valid name");
    _expect(failures, !IdentifierValidation.IsColumnNameValid("a_very_long_column_name", limits),
      "IsColumnNameValid disagreed with ValidateColumnName on an oversized name");
    _expect(failures, IdentifierValidation.IsIndexNameValid("ix_id", limits),
      "IsIndexNameValid disagreed with ValidateIndexName on a valid name");
    _expect(failures, !IdentifierValidation.IsIndexNameValid("a_very_long_index_name", limits),
      "IsIndexNameValid disagreed with ValidateIndexName on an oversized name");

    return failures;
  }

  /// <summary>
  /// Records <paramref name="whatFailed"/> unless <paramref name="holds"/>.
  /// </summary>
  /// <remarks>
  /// Every check reports through this one line so that the arm which only runs when a merged copy
  /// has diverged exists once per host rather than once per check. That arm is unreachable in a
  /// build whose copies agree -- which is the only build that ships -- so each duplicate of it was
  /// a permanently uncovered line, multiplied by the five hosts this assembly is merged into.
  /// </remarks>
  private static void _expect(List<string> failures, bool holds, string whatFailed) {
    if (!holds) {
      failures.Add(whatFailed);
    }
  }

  private static void _expectValid(List<string> failures, string kind, string? result) =>
    _expect(failures, result == null, $"{kind}: a name within the limit was rejected -- {result}");

  private static void _expectRejected(List<string> failures, string kind, string? result) {
    _expect(failures, result != null, $"{kind}: a name over the limit was accepted");
    // Skipped when the name was wrongly accepted at all -- the line above already reported that,
    // and a second message about a missing provider name in a null result would only mislead.
    _expect(failures,
      result == null || result.IndexOf("SelfTest", System.StringComparison.Ordinal) >= 0,
      $"{kind}: the message must name the provider so the author knows which limit -- got '{result}'");
  }
}
