using Whizbang.Data.EFCore.Postgres.Configuration;

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
/// <docs>fundamentals/perspectives/physical-fields#index-advisories</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/JsonbContainmentSwitchTests.cs</tests>
public static class JsonbContainmentSwitch {
  private static volatile bool _enabled = true;

  /// <summary>
  /// Whether the containment rewrite is currently in force. Defaults to <c>true</c>.
  /// </summary>
  public static bool Enabled => _enabled;

  /// <summary>
  /// Applies operator configuration, normally once at startup.
  /// </summary>
  /// <param name="options">The bound options.</param>
  public static void ApplyRuntimeConfiguration(PerspectiveQueryTranslationOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    _enabled = options.UseJsonbContainment;
  }

  /// <summary>
  /// Sets the switch directly, for a host that has no options binding and for tests.
  /// </summary>
  /// <param name="enabled">Whether to compile equality filters into containment tests.</param>
  public static void Set(bool enabled) => _enabled = enabled;

  /// <summary>Restores the default, which is on.</summary>
  public static void Reset() => _enabled = true;
}
