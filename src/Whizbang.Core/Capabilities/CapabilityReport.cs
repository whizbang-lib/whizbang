// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Capabilities;

/// <summary>
/// The startup verdict, in one format for every provider: per provider and scope, one Information block
/// listing exactly the usable capabilities with their purpose, and one Debug block listing the unusable
/// ones with their state, the provider's detail and the fix.
/// </summary>
/// <remarks>
/// A line reads <c>provider/name (State[, version][; detail][: reason]): purpose</c>, and an unusable one
/// adds <c>. Fix: ...</c>. Later slices add the features that require each capability and the setting
/// that turned them on.
/// </remarks>
/// <docs>operations/infrastructure/capabilities#verdict</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityReportTests.cs</tests>
public static partial class CapabilityReport {
  /// <summary>The usable capabilities, one indented line each.</summary>
  /// <param name="statuses">The statuses to report.</param>
  /// <returns>The lines, or <c>(none)</c> when nothing is usable.</returns>
  public static string FormatUsable(IEnumerable<CapabilityStatus> statuses) {
    ArgumentNullException.ThrowIfNull(statuses);
    var lines = statuses.Where(s => s.IsUsable).Select(s => $"  {_head(s)}: {s.Descriptor.Purpose}").ToArray();
    return lines.Length == 0 ? "  (none)" : string.Join('\n', lines);
  }

  /// <summary>The unusable capabilities, one indented line each, with the fix.</summary>
  /// <param name="statuses">The statuses to report.</param>
  /// <returns>The lines; empty when everything is usable.</returns>
  public static string FormatUnusable(IEnumerable<CapabilityStatus> statuses) {
    ArgumentNullException.ThrowIfNull(statuses);
    return string.Join('\n', statuses.Where(s => !s.IsUsable).Select(s =>
      $"  {_head(s)}: {s.Descriptor.Purpose}. Fix: {s.Descriptor.Remedy(s.State)}"));
  }

  /// <summary>Writes the verdict: one block pair per provider and scope, in the order given.</summary>
  /// <param name="logger">Where it goes.</param>
  /// <param name="statuses">The statuses to report.</param>
  public static void Log(ILogger logger, IEnumerable<CapabilityStatus> statuses) {
    ArgumentNullException.ThrowIfNull(logger);
    ArgumentNullException.ThrowIfNull(statuses);
    foreach (var group in statuses.GroupBy(s => (s.Descriptor.Id.Provider, s.Scope))) {
      var scoped = group.ToArray();
      // Formatted only for a level that is on. CA1873 fires on the arguments even with the IsEnabled
      // guard: the analyzer does not look through the surrounding conditional for a source-generated
      // LoggerMessage (the same suppression as PerspectiveWorker's cooldown diagnostic).
#pragma warning disable CA1873
      if (logger.IsEnabled(LogLevel.Information)) {
        LogUsable(logger, group.Key.Provider, group.Key.Scope, scoped.Count(s => s.IsUsable), scoped.Length, FormatUsable(scoped));
      }
      if (logger.IsEnabled(LogLevel.Debug) && scoped.Any(s => !s.IsUsable)) {
        LogUnusable(logger, group.Key.Provider, group.Key.Scope, FormatUnusable(scoped));
      }
#pragma warning restore CA1873
    }
  }

  private static string _head(CapabilityStatus status) {
    var state = status.State.ToString();
    if (status.Version is not null) {
      state += $", {status.Version}";
    }
    if (status.Detail is not null) {
      state += $"; {status.Detail}";
    }
    if (status.Reason is not null) {
      state += $": {status.Reason}";
    }
    return $"{status.Descriptor.Id} ({state})";
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Capabilities of {Provider} on '{Scope}': {Usable} usable of {Known}\n{Capabilities}")]
  static partial void LogUsable(ILogger logger, string provider, string scope, int usable, int known, string capabilities);

  [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
    Message = "Capabilities of {Provider} on '{Scope}' that are not usable\n{Capabilities}")]
  static partial void LogUnusable(ILogger logger, string provider, string scope, string capabilities);
}
