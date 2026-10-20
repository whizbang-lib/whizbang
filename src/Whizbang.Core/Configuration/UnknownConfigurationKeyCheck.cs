// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Configuration;

/// <summary>A configured key nothing reads.</summary>
/// <param name="Key">The key as configured.</param>
/// <param name="Removed">The removed-key entry when the key was once read, else <see langword="null"/>.</param>
/// <docs>operations/configuration/configuration-reference#unknown-keys</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/UnknownConfigurationKeyCheckTests.cs</tests>
internal sealed record UnknownConfigurationKey(string Key, RemovedConfigurationKey? Removed);

/// <summary>
/// The check of configured <c>Whizbang:*</c> keys against the keys Whizbang reads. .NET binding ignores a
/// key no options class binds, so without this a removed or misspelled key leaves the setting at its
/// default with no sign of it.
/// </summary>
/// <remarks>
/// Only the <c>Whizbang</c> section is checked; keys outside it belong to the host. Only keys that carry
/// a value count: a section is the path to its keys, not a setting.
/// </remarks>
/// <docs>operations/configuration/configuration-reference#unknown-keys</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/UnknownConfigurationKeyCheckTests.cs</tests>
internal static partial class UnknownConfigurationKeyCheck {
  /// <summary>The section the check covers.</summary>
  internal const string SECTION = "Whizbang";

  /// <summary>The key that selects the <see cref="UnknownConfigurationKeyMode"/>.</summary>
  internal const string MODE_KEY = "Whizbang:Configuration:UnknownKeys";

  /// <summary>The configured <c>Whizbang:*</c> keys nothing reads, ordered by key.</summary>
  /// <param name="configuration">The host's configuration.</param>
  /// <param name="known">The keys Whizbang reads.</param>
  /// <returns>The unknown keys; empty when every key is read.</returns>
  public static IReadOnlyList<UnknownConfigurationKey> Find(IConfiguration configuration, KnownConfigurationKeys known) {
    ArgumentNullException.ThrowIfNull(configuration);
    ArgumentNullException.ThrowIfNull(known);
    return [.. configuration.GetSection(SECTION).AsEnumerable()
      .Where(pair => pair.Value is not null && !known.IsKnown(pair.Key))
      .Select(pair => new UnknownConfigurationKey(pair.Key, RemovedConfigurationKeys.Find(pair.Key)))
      .OrderBy(unknown => unknown.Key, StringComparer.OrdinalIgnoreCase)];
  }

  /// <summary>
  /// Reports the unknown keys as <paramref name="mode"/> says: one Warning per key (naming the
  /// replacement of a removed key), then, for <see cref="UnknownConfigurationKeyMode.Throw"/>, an
  /// exception naming every key.
  /// </summary>
  /// <param name="configuration">The host's configuration; <see langword="null"/> checks nothing.</param>
  /// <param name="known">The keys Whizbang reads.</param>
  /// <param name="mode">What to do with an unknown key.</param>
  /// <param name="logger">Where the warnings go.</param>
  /// <exception cref="InvalidOperationException"><paramref name="mode"/> is Throw and a key is unknown.</exception>
  public static void Run(IConfiguration? configuration, KnownConfigurationKeys known, UnknownConfigurationKeyMode mode, ILogger logger) {
    ArgumentNullException.ThrowIfNull(logger);
    if (configuration is null || mode == UnknownConfigurationKeyMode.Ignore) {
      return;
    }

    var unknown = Find(configuration, known);
    foreach (var entry in unknown) {
      if (entry.Removed is { } removed) {
        var replacement = string.Join(", ", removed.ReplacedBy);
        LogRemovedKey(logger, entry.Key, replacement);
      } else {
        LogUnknownKey(logger, entry.Key);
      }
    }

    if (mode == UnknownConfigurationKeyMode.Throw && unknown.Count > 0) {
      throw new InvalidOperationException(
        "Whizbang configuration sets keys that Whizbang does not read: "
        + string.Join(", ", unknown.Select(entry => entry.Key))
        + $". Remove or correct them, or set {MODE_KEY} to Warn or Ignore.");
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Whizbang configuration key '{Key}' is not read by Whizbang and has no effect. Check its spelling against the configuration reference, or remove it")]
  private static partial void LogUnknownKey(ILogger logger, string key);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "Whizbang configuration key '{Key}' was removed and has no effect. Set its replacement instead: {Replacement}")]
  private static partial void LogRemovedKey(ILogger logger, string key, string replacement);
}
