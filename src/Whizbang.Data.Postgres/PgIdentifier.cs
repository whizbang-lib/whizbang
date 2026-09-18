namespace Whizbang.Data.Postgres;

/// <summary>
/// Quotes PostgreSQL identifiers for the one case that cannot be parameterized: a schema or object
/// name interpolated into SQL text.
/// </summary>
/// <remarks>
/// <para>
/// Values are always bound as parameters, but an identifier cannot be — <c>SELECT @p()</c> is not a
/// function call — so every schema-qualified statement in the data packages interpolates its schema
/// name. That interpolation was written independently in ten places, each as
/// <c>$"\"{schema}\".{name}"</c>, and none of them escaped a quote inside the name.
/// PostgreSQL escapes a quote inside a quoted identifier by doubling it, so an unescaped one closes
/// the identifier early and everything after it is parsed as SQL.
/// </para>
/// <para>
/// The schema usually comes from the EF Core model and is a developer constant, which is what the
/// suppressions on those call sites said. It is not always: per-schema multi-tenancy is a supported
/// deployment, and there the schema is chosen per tenant. One helper, used everywhere, removes the
/// question of whether any given call site is the exception.
/// </para>
/// </remarks>
internal static class PgIdentifier {

  /// <summary>The schema every deployment falls back to when none is configured.</summary>
  internal const string PUBLIC_SCHEMA = "public";

  /// <summary>
  /// Returns <paramref name="identifier"/> as a quoted PostgreSQL identifier, doubling any quote it
  /// contains so it cannot terminate the quoting early.
  /// </summary>
  internal static string Quote(string identifier) {
    ArgumentNullException.ThrowIfNull(identifier);
    return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
  }

  /// <summary>
  /// Returns the schema prefix to put in front of an object name — <c>"schema".</c> — or an empty
  /// string when the schema is absent or <c>public</c>, so the caller never emits a leading dot.
  /// </summary>
  internal static string QualifyPrefix(string? schema)
    => string.IsNullOrWhiteSpace(schema) || schema == PUBLIC_SCHEMA ? string.Empty : $"{Quote(schema)}.";

  /// <summary>
  /// Returns <paramref name="identifier"/> if it is a bare PostgreSQL identifier — letters, digits
  /// and underscores only — and throws otherwise.
  /// </summary>
  /// <remarks>
  /// For the case that cannot be quoted: an identifier read from a table and interpolated UNQUOTED,
  /// where quoting would change its meaning because PostgreSQL folds an unquoted name to lower case
  /// and takes a quoted one literally. Validation keeps the existing folding behavior and fails
  /// closed. Mirrors the check the schema initializer already applies to DDL identifiers.
  /// </remarks>
  /// <exception cref="ArgumentException">The identifier contains anything else.</exception>
  internal static string RequireBare(string identifier, string paramName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(identifier, paramName);
    foreach (var c in identifier) {
      if (!char.IsLetterOrDigit(c) && c != '_') {
        throw new ArgumentException(
          $"'{identifier}' is not a bare SQL identifier. It is interpolated into SQL unquoted, so only "
          + "letters, digits and underscores are accepted.", paramName);
      }
    }
    return identifier;
  }

  /// <summary>
  /// Returns <paramref name="name"/> qualified by <paramref name="schema"/>, or bare when the schema
  /// is absent or <c>public</c>. <paramref name="name"/> is a compile-time constant at every call
  /// site and is emitted unquoted, matching the SQL these packages have always produced.
  /// </summary>
  internal static string Qualify(string? schema, string name) {
    ArgumentNullException.ThrowIfNull(name);
    return QualifyPrefix(schema) + name;
  }
}
