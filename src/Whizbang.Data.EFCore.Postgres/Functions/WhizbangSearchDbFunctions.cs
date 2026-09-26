using Microsoft.EntityFrameworkCore;

namespace Whizbang.Data.EFCore.Postgres.Functions;

/// <summary>
/// The folded substring search as a query function, for use in a LINQ query over a perspective.
/// </summary>
/// <remarks>
/// A <c>Contains</c> on a field declared <c>[Indexed(IndexKinds.Search)]</c> is translated to this
/// automatically. Call it directly to search a field that is not declared (it then scans), or to be explicit.
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#search</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/SearchQueryShapeTests.cs</tests>
public static class WhizbangSearchDbFunctions {

  /// <summary>
  /// True when <paramref name="term"/> occurs in <paramref name="value"/>, ignoring case and typographic
  /// variants of quotes, dashes and spaces. Translated to <c>wh_fold(value) LIKE wh_fold_pattern(term)</c>.
  /// </summary>
  /// <param name="_">The <see cref="EF.Functions"/> instance.</param>
  /// <param name="value">The field to search.</param>
  /// <param name="term">What to look for; LIKE wildcards in it match literally.</param>
  /// <returns>Only meaningful inside a query; throws when called directly.</returns>
  public static bool FoldedContains(this DbFunctions _, string? value, string term) =>
    throw new InvalidOperationException(
      $"{nameof(FoldedContains)} is translated to SQL inside a query and cannot be evaluated in memory.");
}
