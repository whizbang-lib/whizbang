// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Archival;

/// <summary>
/// The process-wide policy for rows of archived streams, per perspective (#1739): what each perspective declares with
/// <see cref="Attributes.ArchivedStreamRowsAttribute"/>, registered by generated <c>[ModuleInitializer]</c> code, and what
/// <c>Whizbang:Perspectives:ArchivedStreamRows</c> overrides.
/// </summary>
/// <remarks>
/// <para>
/// Two readers. The generated runner asks <see cref="RemovesRows"/> on a missing row: a perspective whose rows an archive
/// removes probes the log for history and rebuilds the row from it, exactly as a row that retention evicted is rebuilt.
/// The SQL archive reads the policies from <c>wh_settings</c> (<see cref="SETTING_KEY"/>), which the maintenance worker
/// writes on every tick from <see cref="Effective"/> and <see cref="SettingFor"/>, because the archive runs inside the
/// database, where this process's options cannot reach.
/// </para>
/// <para>
/// Names are perspective CLR type names (<c>TypeNameFormatter.FormatClrTypeName</c>), the form cursors and snapshots are
/// keyed by. Self-registration rather than assembly scanning is an AOT requirement: discovering attributes by scanning
/// needs reflection.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/archive-store#perspective-rows</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/ArchivedStreamRowPoliciesTests.cs</tests>
public static class ArchivedStreamRowPolicies {
  /// <summary>The <c>wh_settings</c> key the SQL archive reads the policies from: one <c>name=policy</c> per line.</summary>
  public const string SETTING_KEY = "archived_stream_rows";

  private static readonly char[] _unstorable = ['=', '\n', '\r'];
  private static readonly ConcurrentDictionary<string, ArchivedStreamRowPolicy> _declared = new(StringComparer.Ordinal);
  private static volatile Dictionary<string, ArchivedStreamRowPolicy>? _overrides;

  /// <summary>
  /// Registers a perspective's declared policy. Idempotent; the last registration wins. Called from generated module
  /// initializers.
  /// </summary>
  /// <param name="perspectiveName">The perspective's CLR type name.</param>
  /// <param name="policy">The declared policy.</param>
  /// <exception cref="ArgumentException">The name is empty, or holds <c>=</c> or a line break, which the stored setting
  /// cannot carry.</exception>
  public static void Register(string perspectiveName, ArchivedStreamRowPolicy policy) {
    ThrowIfUnstorable(perspectiveName);
    _declared[perspectiveName] = policy;
  }

  /// <summary>
  /// Applies the operator's overrides (<see cref="ArchivedStreamRowsOptions.Perspectives"/>), which outrank the
  /// declarations. Called at startup by <see cref="ArchivedStreamRowsConfigurator"/>; <c>null</c> or an empty set restores
  /// the declarations alone.
  /// </summary>
  /// <param name="overrides">Each overridden perspective's policy, by CLR type name.</param>
  public static void ApplyRuntimeConfiguration(IReadOnlyDictionary<string, ArchivedStreamRowPolicy>? overrides) =>
    _overrides = overrides is null or { Count: 0 }
      ? null
      : new Dictionary<string, ArchivedStreamRowPolicy>(overrides, StringComparer.Ordinal);

  /// <summary>
  /// The perspective's effective policy: the applied override, else its declaration, else
  /// <see cref="ArchivedStreamRowPolicy.Keep"/>.
  /// </summary>
  /// <param name="perspectiveName">The perspective's CLR type name.</param>
  /// <returns>The policy that applies to the perspective's rows of archived streams.</returns>
  public static ArchivedStreamRowPolicy Resolve(string perspectiveName) {
    if (_overrides is { } overrides && overrides.TryGetValue(perspectiveName, out var overridden)) {
      return overridden;
    }
    return _declared.TryGetValue(perspectiveName, out var declared) ? declared : ArchivedStreamRowPolicy.Keep;
  }

  /// <summary>
  /// Whether an archive can take the perspective's row out of its table (<see cref="ArchivedStreamRowPolicy.Drop"/> or
  /// <see cref="ArchivedStreamRowPolicy.Archive"/>), so a missing row may be one an archive removed rather than a new
  /// stream. The generated runner asks it on a missing row.
  /// </summary>
  /// <param name="perspectiveName">The perspective's CLR type name.</param>
  /// <returns><c>true</c> when the effective policy is not <see cref="ArchivedStreamRowPolicy.Keep"/>.</returns>
  public static bool RemovesRows(string perspectiveName) => Resolve(perspectiveName) != ArchivedStreamRowPolicy.Keep;

  /// <summary>
  /// Every declared or overridden perspective with its effective policy, <paramref name="overrides"/> winning, ordered by
  /// name. An explicit <see cref="ArchivedStreamRowPolicy.Keep"/> is listed too: it outvotes another perspective of the
  /// same model that drops or archives.
  /// </summary>
  /// <param name="overrides">The configured overrides, or <c>null</c> for none.</param>
  /// <returns>One entry per perspective.</returns>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0305:Collection initialization can be simplified",
    Justification = "ToArray takes the dictionary's own locks; a collection expression reads Count, then copies, and the copy throws when a registration lands in between.")]
  public static IReadOnlyList<KeyValuePair<string, ArchivedStreamRowPolicy>> Effective(
      IReadOnlyDictionary<string, ArchivedStreamRowPolicy>? overrides) {
    var merged = new SortedDictionary<string, ArchivedStreamRowPolicy>(StringComparer.Ordinal);
    foreach (var (name, policy) in _declared.ToArray()) {
      merged[name] = policy;
    }
    foreach (var (name, policy) in overrides ?? new Dictionary<string, ArchivedStreamRowPolicy>()) {
      merged[name] = policy;
    }
    return [.. merged];
  }

  /// <summary>
  /// The <c>wh_settings</c> row the SQL archive reads: one <c>name=policy</c> per line, written every time (empty for
  /// none) so a policy removed from the code or the configuration stops applying.
  /// </summary>
  /// <param name="policies">The effective policies (<see cref="Effective"/>).</param>
  /// <returns>The setting to publish.</returns>
  public static RetentionSetting SettingFor(IReadOnlyList<KeyValuePair<string, ArchivedStreamRowPolicy>> policies) {
    ArgumentNullException.ThrowIfNull(policies);
    return new RetentionSetting(
      SETTING_KEY, string.Join('\n', policies.Select(static p => $"{p.Key}={p.Value}")), "string", ArchivedStreamRowsOptions.SECTION);
  }

  /// <summary>Refuses a name the stored setting cannot carry.</summary>
  /// <param name="perspectiveName">The name.</param>
  internal static void ThrowIfUnstorable(string perspectiveName) {
    ArgumentException.ThrowIfNullOrEmpty(perspectiveName);
    if (perspectiveName.IndexOfAny(_unstorable) >= 0) {
      throw new ArgumentException(
        $"'{perspectiveName}' cannot name a perspective: the stored policies are one name=policy per line.", nameof(perspectiveName));
    }
  }
}
