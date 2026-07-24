namespace Whizbang.Core.Naming;

/// <summary>
/// Central, reusable name-derivation helpers Whizbang uses to keep its
/// turnkey wiring coherent across the source generator, runtime DI, and
/// any user code that wants to follow the same convention.
/// </summary>
/// <remarks>
/// <para>
/// The motivating bug — fixed by introducing this class — was a silent
/// divergence between two implementations of the same convention. The
/// EF source generator derived the connection-string key one way; the
/// runtime PostConfigure derived it the same way <em>in source code</em>
/// but read from a different field at runtime, producing a different
/// value. The two halves of the turnkey path disagreed and the notification
/// workers fell back to the pooled (pgbouncer) connection while EF used
/// the correct one.
/// </para>
/// <para>
/// Every caller must funnel through these helpers. The companion
/// <c>WhizbangNamingConventionTests</c> + the source-generator regression
/// test pin the outputs character-for-character so a future change
/// surfaces on the test report instead of in a production log.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public static class WhizbangNamingConvention {

  /// <summary>
  /// Derives the <c>ConnectionStrings:{Name}</c> key Whizbang uses by default
  /// for a given <c>DbContext</c> class name. The convention strips a trailing
  /// <c>"DbContext"</c> suffix, lowercases the remainder, and appends
  /// <c>"-db"</c>.
  /// </summary>
  /// <param name="dbContextClassName">The simple name of the DbContext class
  /// (e.g., <c>"BffServiceDbContext"</c>, <c>"ChatDbContext"</c>).</param>
  /// <returns>The convention-derived key
  /// (e.g., <c>"appservice-db"</c>, <c>"chat-db"</c>).</returns>
  /// <remarks>
  /// Examples:
  /// <list type="bullet">
  /// <item><description><c>"BffServiceDbContext"</c> → <c>"appservice-db"</c></description></item>
  /// <item><description><c>"ChatDbContext"</c> → <c>"chat-db"</c></description></item>
  /// <item><description><c>"InventoryDbContext"</c> → <c>"inventory-db"</c></description></item>
  /// <item><description><c>"Foo"</c> (no suffix) → <c>"foo-db"</c></description></item>
  /// </list>
  /// </remarks>
  public static string DeriveConnectionStringName(string dbContextClassName) {
    ArgumentNullException.ThrowIfNull(dbContextClassName);
    if (dbContextClassName.Length == 0) {
      throw new ArgumentException("DbContext class name must be non-empty.", nameof(dbContextClassName));
    }
    var name = dbContextClassName.EndsWith("DbContext", StringComparison.Ordinal)
      ? dbContextClassName[..^9]
      : dbContextClassName;
    return name.ToLowerInvariant() + "-db";
  }
}
