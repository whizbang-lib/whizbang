using Whizbang.Data.EFCore.Postgres.Configuration;
using Whizbang.Data.EFCore.Postgres.QueryTranslation.Containment;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation;

/// <summary>
/// The consult point for whether a perspective equality filter is compiled into a jsonb containment
/// test. On by default, and switchable at run time so a deployment can fall back without a release.
/// </summary>
/// <remarks>
/// <para>
/// The query pipeline reads this on every compilation rather than caching it, so flipping it takes
/// effect for queries compiled after the change. Entity Framework caches compiled queries, so a
/// query already compiled keeps the form it was compiled with until its cache entry is evicted;
/// flip the switch at startup to be certain of the shape from the first query onward.
/// </para>
/// <para>
/// This mirrors how perspective row retention is configured: options bind from configuration and are
/// applied to a static consult point at startup, so every seam agrees without threading an options
/// object through the query pipeline.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/JsonbContainmentSwitchTests.cs</tests>
public static class JsonbContainmentSwitch {
  private static volatile ContainmentMode _mode = ContainmentMode.ExpressionTree;

  /// <summary>
  /// Which mechanism is in force. Defaults to <see cref="ContainmentMode.ExpressionTree"/>.
  /// </summary>
  public static ContainmentMode Mode => _mode;

  /// <summary>
  /// Whether any mechanism is in force, which is what most consult points actually ask.
  /// </summary>
  public static bool Enabled => _mode != ContainmentMode.Off;

  /// <summary>Whether the LINQ tree rewrite is the mechanism selected.</summary>
  /// <remarks>
  /// Asked by the mechanism itself rather than inferred from <see cref="Enabled"/>, so that exactly
  /// one ever acts. Both acting would compile a filter twice.
  /// </remarks>
  public static bool RewritesExpressionTree => _mode == ContainmentMode.ExpressionTree;

  /// <summary>Whether reshaping the translated query is the mechanism selected.</summary>
  public static bool ReshapesTranslatedTree => _mode == ContainmentMode.TranslatedTree;

  /// <summary>
  /// Applies operator configuration, normally once at startup.
  /// </summary>
  /// <param name="options">The bound options.</param>
  /// <remarks>
  /// Configuration written before a mode existed said only whether the rewrite was on, and still
  /// means that: turning it off turns everything off, and leaving it on selects the mechanism named
  /// by the mode, which itself defaults to the one that has shipped.
  /// </remarks>
  public static void ApplyRuntimeConfiguration(PerspectiveQueryTranslationOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    _mode = options.UseJsonbContainment ? options.ContainmentMode : ContainmentMode.Off;
  }

  /// <summary>
  /// Sets the switch directly, for a host that has no options binding and for tests.
  /// </summary>
  /// <param name="enabled">Whether to compile equality filters into containment tests.</param>
  public static void Set(bool enabled) =>
    _mode = enabled ? ContainmentMode.ExpressionTree : ContainmentMode.Off;

  /// <summary>Selects a mechanism directly, for tests that exercise one in particular.</summary>
  /// <param name="mode">The mechanism to put in force.</param>
  public static void SetMode(ContainmentMode mode) => _mode = mode;

  /// <summary>Restores the default, which is the mechanism that has shipped.</summary>
  public static void Reset() => _mode = ContainmentMode.ExpressionTree;
}
