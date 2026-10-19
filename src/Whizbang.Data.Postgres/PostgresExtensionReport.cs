// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The startup report: for each database, one Information block listing exactly the usable extensions
/// and what each enables, and one Debug block listing the unusable ones with their state and the fix.
/// </summary>
/// <remarks>
/// A database the probe could not read writes nothing here: the probe's own warning already names it
/// and the reason, and a block of Unknown lines would say nothing more.
/// </remarks>
/// <docs>operations/infrastructure/postgres-extensions#startup-report</docs>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/PostgresExtensionReportTests.cs</tests>
public static partial class PostgresExtensionReport {
  /// <summary>The usable extensions, one indented line each: name, version when installed, state, purpose.</summary>
  /// <param name="database">One database's answer.</param>
  /// <returns>The lines, or <c>(none)</c> when nothing is usable.</returns>
  public static string FormatUsable(PostgresDatabaseExtensions database) {
    ArgumentNullException.ThrowIfNull(database);
    var lines = database.Extensions.Where(e => e.IsUsable).Select(_usableLine).ToArray();
    return lines.Length == 0 ? "  (none)" : string.Join('\n', lines);
  }

  /// <summary>The unusable extensions, one indented line each: name, state, purpose and the fix.</summary>
  /// <param name="database">One database's answer.</param>
  /// <returns>The lines; empty when every extension is usable.</returns>
  public static string FormatUnusable(PostgresDatabaseExtensions database) {
    ArgumentNullException.ThrowIfNull(database);
    return string.Join('\n', database.Extensions.Where(e => !e.IsUsable).Select(e =>
      $"  {e.Extension.Name} ({e.State}): {e.Extension.Enables}. Fix: {PostgresExtensionCatalog.Remedy(e.Extension, e.State)}"));
  }

  /// <summary>Writes the report for one database.</summary>
  /// <param name="logger">Where it goes.</param>
  /// <param name="database">One database's answer.</param>
  public static void Log(ILogger logger, PostgresDatabaseExtensions database) {
    ArgumentNullException.ThrowIfNull(logger);
    ArgumentNullException.ThrowIfNull(database);
    if (database.ProbeFailure is not null) {
      return;
    }
    // The blocks are formatted only for a level that is on. CA1873 fires on the arguments even with the
    // IsEnabled guard: the analyzer does not look through the surrounding conditional for a
    // source-generated LoggerMessage (the same suppression as PerspectiveWorker's cooldown diagnostic).
#pragma warning disable CA1873
    if (logger.IsEnabled(LogLevel.Information)) {
      LogUsable(logger, database.Database, database.Extensions.Count(e => e.IsUsable), database.Extensions.Count, FormatUsable(database));
    }
    if (logger.IsEnabled(LogLevel.Debug) && database.Extensions.Any(e => !e.IsUsable)) {
      LogUnusable(logger, database.Database, FormatUnusable(database));
    }
#pragma warning restore CA1873
  }

  private static string _usableLine(PostgresExtensionStatus status) => status.InstalledVersion is null
    ? $"  {status.Extension.Name} ({status.State}): {status.Extension.Enables}"
    : $"  {status.Extension.Name} {status.InstalledVersion} ({status.State}): {status.Extension.Enables}";

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Postgres extensions on database '{Database}': {Usable} usable of {Known} known\n{Extensions}")]
  static partial void LogUsable(ILogger logger, string database, int usable, int known, string extensions);

  [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
    Message = "Postgres extensions on database '{Database}' that are not usable here\n{Extensions}")]
  static partial void LogUnusable(ILogger logger, string database, string extensions);
}
