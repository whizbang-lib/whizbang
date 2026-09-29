using System;

namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// The one statement every perspective index is created through.
/// </summary>
/// <remarks>
/// <para>
/// <c>CREATE INDEX IF NOT EXISTS</c> compares names and nothing else. An index an earlier path built
/// under its own name, with the same definition as one the schema declares, was therefore joined by
/// a twin, and both were written on every change to the row. The statement here hands the create to
/// <c>wh_ensure_index</c>, which reads the table's existing indexes and creates the declared one only
/// when none of them has the same definition.
/// </para>
/// <para>
/// The DDL is passed through unchanged, dollar-quoted, so a reader of the schema script sees exactly
/// the index that would be built and nothing in it has to be escaped. Anything other than an
/// idempotent create is returned as it is: a drop is an explicit decision, and the function only
/// compares what it would create.
/// </para>
/// <para>
/// A statement inside an optional-extension block is not routed through here. The block's reader
/// names the indexes it skips by reading their create statements, and those indexes need an
/// extension a server may refuse, so the block keeps its plain statements.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-indexes</docs>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveIndexSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/EnsureIndexFunctionTests.cs</tests>
public static class PerspectiveIndexSql {
  /// <summary>The function the statement calls, defined by migration 174.</summary>
  public const string FUNCTION = "wh_ensure_index";

  /// <summary>The dollar quote around the DDL. Chosen so no index definition can contain it.</summary>
  public const string DDL_QUOTE = "$wbix$";

  private const string CREATE_INDEX = "CREATE INDEX IF NOT EXISTS ";
  private const string CREATE_UNIQUE_INDEX = "CREATE UNIQUE INDEX IF NOT EXISTS ";

  /// <summary>
  /// The statement that creates <paramref name="statement"/>'s index only when the table has no
  /// equivalent, or <paramref name="statement"/> itself when it is not an idempotent create.
  /// </summary>
  /// <param name="statement">One <c>CREATE [UNIQUE] INDEX IF NOT EXISTS</c> statement, with or without its semicolon.</param>
  /// <param name="quotedSchema">The schema the function lives in, quoted as the schema pass quotes it.</param>
  /// <returns>One statement, ending in a semicolon when the input was a create.</returns>
  public static string Ensure(string statement, string quotedSchema) {
    if (statement is null) {
      throw new ArgumentNullException(nameof(statement));
    }

    var ddl = statement.Trim();
    if (!ddl.StartsWith(CREATE_INDEX, StringComparison.Ordinal)
        && !ddl.StartsWith(CREATE_UNIQUE_INDEX, StringComparison.Ordinal)) {
      return statement;
    }

    ddl = ddl.TrimEnd(';').TrimEnd();
    return $"SELECT {quotedSchema}.{FUNCTION}({DDL_QUOTE}{ddl}{DDL_QUOTE});";
  }
}
