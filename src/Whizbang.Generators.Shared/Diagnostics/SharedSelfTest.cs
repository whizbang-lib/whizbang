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
    if (IdentifierValidation.GetByteCount("abc") != 3) {
      failures.Add("GetByteCount: ASCII is one byte per character");
    }
    if (IdentifierValidation.GetByteCount("é") != 2) {
      failures.Add("GetByteCount: a two-byte character must count as two, not one");
    }
    if (IdentifierValidation.GetByteCount("") != 0) {
      failures.Add("GetByteCount: an empty identifier is zero bytes");
    }

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
    if (!IdentifierValidation.IsTableNameValid("orders", limits)) {
      failures.Add("IsTableNameValid disagreed with ValidateTableName on a valid name");
    }
    if (IdentifierValidation.IsTableNameValid("a_very_long_table_name", limits)) {
      failures.Add("IsTableNameValid disagreed with ValidateTableName on an oversized name");
    }
    if (!IdentifierValidation.IsColumnNameValid("id", limits)) {
      failures.Add("IsColumnNameValid disagreed with ValidateColumnName on a valid name");
    }
    if (IdentifierValidation.IsColumnNameValid("a_very_long_column_name", limits)) {
      failures.Add("IsColumnNameValid disagreed with ValidateColumnName on an oversized name");
    }
    if (!IdentifierValidation.IsIndexNameValid("ix_id", limits)) {
      failures.Add("IsIndexNameValid disagreed with ValidateIndexName on a valid name");
    }
    if (IdentifierValidation.IsIndexNameValid("a_very_long_index_name", limits)) {
      failures.Add("IsIndexNameValid disagreed with ValidateIndexName on an oversized name");
    }

    return failures;
  }

  private static void _expectValid(List<string> failures, string kind, string? result) {
    if (result != null) {
      failures.Add($"{kind}: a name within the limit was rejected -- {result}");
    }
  }

  private static void _expectRejected(List<string> failures, string kind, string? result) {
    if (result == null) {
      failures.Add($"{kind}: a name over the limit was accepted");
    } else if (result.IndexOf("SelfTest", System.StringComparison.Ordinal) < 0) {
      failures.Add($"{kind}: the message must name the provider so the author knows which limit -- got '{result}'");
    }
  }
}
