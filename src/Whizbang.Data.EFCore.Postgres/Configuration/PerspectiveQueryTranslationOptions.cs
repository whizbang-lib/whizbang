namespace Whizbang.Data.EFCore.Postgres.Configuration;

/// <summary>
/// Operator configuration for how a perspective filter is compiled to SQL.
/// </summary>
/// <remarks>
/// <para>
/// The one setting here is a kill switch. Compiling an equality filter into a jsonb containment test
/// is what lets the GIN index on the data column answer it, and it is on by default because the
/// alternative is a sequential scan on every filtered perspective. It is still a change to the SQL
/// that every consumer's queries produce, so there has to be a way to put it back without waiting
/// for a release.
/// </para>
/// <para>
/// Bind it from configuration and apply it at startup, the same way perspective row retention is
/// applied. Turning it off restores the extraction form everywhere, immediately and for every
/// perspective, with no other behavioral change.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#index-advisories</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/JsonbContainmentSwitchTests.cs</tests>
public sealed class PerspectiveQueryTranslationOptions {
  /// <summary>
  /// Whether an equality filter on a JSON-only property is compiled into a containment test that a
  /// GIN index can answer (default <c>true</c>).
  /// </summary>
  /// <remarks>
  /// Set to <c>false</c> to fall back to extracting the value out of the document per row, which is
  /// what the framework did before. Correctness is unaffected either way; only the plan changes.
  /// </remarks>
  public bool UseJsonbContainment { get; set; } = true;
}
