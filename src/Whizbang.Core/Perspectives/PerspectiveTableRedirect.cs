namespace Whizbang.Core.Perspectives;

/// <summary>
/// Sends the perspective store's reads and writes for one table to another table, for the duration of an
/// async flow: how a blue-green rebuild replays into its shadow table while every other flow keeps reading
/// and writing the live one.
/// </summary>
/// <remarks>
/// <para>
/// Ambient rather than a store parameter for the same reason <c>ProcessingModeAccessor</c> is: the
/// rebuilder drives generated runners through <see cref="IPerspectiveRunner"/>, and the store a runner
/// writes through is created by the runner's own scope. A store resolves its table through
/// <see cref="Resolve"/> on every statement, so a redirect set by the rebuilder reaches it without any
/// signature changing, and a flow that never set one is untouched.
/// </para>
/// <para>
/// Matching is by the table's own name, ignoring a schema prefix and double quotes, and the replacement
/// keeps the prefix and quoting it was given. Redirects nest; disposing the handle restores the outer one.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/rebuild#blue-green</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveTableRedirectTests.cs</tests>
public static class PerspectiveTableRedirect {
  private static readonly AsyncLocal<Redirect?> _current = new();

  /// <summary>Whether this flow redirects any table.</summary>
  public static bool IsActive => _current.Value is not null;

  /// <summary>
  /// Redirects <paramref name="table"/> to <paramref name="target"/> in this async flow until the returned
  /// handle is disposed.
  /// </summary>
  /// <param name="table">The table's name, without a schema.</param>
  /// <param name="target">The table to use instead, in the same schema.</param>
  /// <returns>A handle that ends the redirect.</returns>
  public static IDisposable Begin(string table, string target) {
    ArgumentException.ThrowIfNullOrWhiteSpace(table);
    ArgumentException.ThrowIfNullOrWhiteSpace(target);
    var outer = _current.Value;
    _current.Value = new Redirect(table, target, outer);
    return new Handle(outer);
  }

  /// <summary>
  /// The table a statement against <paramref name="table"/> uses in this flow: the redirect target when the
  /// name matches one, otherwise <paramref name="table"/> unchanged.
  /// </summary>
  /// <param name="table">A table name, bare (<c>t</c>) or schema-qualified (<c>s.t</c>, <c>"s"."t"</c>).</param>
  public static string Resolve(string table) {
    ArgumentNullException.ThrowIfNull(table);
    var redirect = _current.Value;
    if (redirect is null) {
      return table;
    }
    var dot = table.LastIndexOf('.');
    var prefix = table[..(dot + 1)];
    var last = table[(dot + 1)..];
    var quoted = last.Length >= 2 && last[0] == '"' && last[^1] == '"';
    var name = quoted ? last[1..^1] : last;
    for (var r = redirect; r is not null; r = r.Outer) {
      if (string.Equals(r.Table, name, StringComparison.Ordinal)) {
        return prefix + (quoted ? "\"" + r.Target + "\"" : r.Target);
      }
    }
    return table;
  }

  private sealed record Redirect(string Table, string Target, Redirect? Outer);

  private sealed class Handle(Redirect? outer) : IDisposable {
    public void Dispose() => _current.Value = outer;
  }
}
