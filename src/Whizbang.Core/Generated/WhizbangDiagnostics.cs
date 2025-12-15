using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Whizbang.Core.Generated;

/// <summary>
/// Central diagnostics aggregator for all Whizbang generators.
/// Collects diagnostic information captured at build time.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Generated/GeneratedDiagnosticsTests.cs:Diagnostics_ShouldCollectReceptorDiscoveryInfoAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Generated/GeneratedDiagnosticsTests.cs:Diagnostics_ShouldCaptureTimestampAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Generated/GeneratedDiagnosticsTests.cs:Diagnostics_ShouldDisplayFormattedOutputAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Generated/GeneratedDiagnosticsTests.cs:Diagnostics_ShouldFilterByCategoryAsync</tests>
[ExcludeFromCodeCoverage]
[DebuggerNonUserCode]
public static class WhizbangDiagnostics {
  private static readonly List<DiagnosticEntry> _entries = [];
  private static readonly Lock _lock = new();

  /// <summary>
  /// Adds a diagnostic entry to the collection.
  /// This is called during static initialization by each generator.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Generated/GeneratedDiagnosticsTests.cs:Diagnostics_ShouldCollectReceptorDiscoveryInfoAsync</tests>
  [ExcludeFromCodeCoverage]
  [DebuggerNonUserCode]
  public static void AddEntry(DiagnosticEntry entry) {
    lock (_lock) {
      _entries.Add(entry);
    }
  }

  /// <summary>
  /// Gets diagnostic information from all generators.
  /// This information was captured at build time.
  /// </summary>
  /// <param name="categories">Filter diagnostics by category. Defaults to All.</param>
  /// <param name="printToConsole">If true, prints the diagnostics to console. Defaults to true.</param>
  /// <returns>Formatted diagnostic information as a string.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Generated/GeneratedDiagnosticsTests.cs:Diagnostics_ShouldCollectReceptorDiscoveryInfoAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Generated/GeneratedDiagnosticsTests.cs:Diagnostics_ShouldCaptureTimestampAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Generated/GeneratedDiagnosticsTests.cs:Diagnostics_ShouldDisplayFormattedOutputAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Generated/GeneratedDiagnosticsTests.cs:Diagnostics_ShouldFilterByCategoryAsync</tests>
  [ExcludeFromCodeCoverage]
  public static string Diagnostics(DiagnosticCategory categories = DiagnosticCategory.All, bool printToConsole = true) {
    var filteredEntries = _entries.Where(e => (e.Category & categories) != 0).ToList();

    if (filteredEntries.Count == 0) {
      var emptyMessage = "No diagnostic information available for the specified categories.";
      if (printToConsole) {
        Console.WriteLine(emptyMessage);
      }
      return emptyMessage;
    }

    var output = new System.Text.StringBuilder();
    output.AppendLine("═══════════════════════════════════════════════════════════════");
    output.AppendLine("Whizbang Source Generators - Build Diagnostics");
    if (categories != DiagnosticCategory.All) {
      output.AppendLine($"Categories: {categories}");
    }
    output.AppendLine("═══════════════════════════════════════════════════════════════");
    output.AppendLine($"Total Generators: {filteredEntries.Count}");
    output.AppendLine("───────────────────────────────────────────────────────────────");
    output.AppendLine();

    foreach (var entry in filteredEntries) {
      output.AppendLine($"[{entry.GeneratorName}]");
      output.AppendLine($"  Timestamp: {entry.Timestamp}");
      output.AppendLine($"  Category:  {entry.Category}");
      output.AppendLine($"  {entry.Message}");
      output.AppendLine();
    }

    output.AppendLine("═══════════════════════════════════════════════════════════════");

    var result = output.ToString();
    if (printToConsole) {
      Console.Write(result);
    }
    return result;
  }

  /// <summary>
  /// Prints all collected diagnostic information from all generators.
  /// This is a convenience method that calls Diagnostics(printToConsole: true).
  /// </summary>
  [ExcludeFromCodeCoverage]
  [Obsolete("Use Diagnostics() instead. This method will be removed in a future version.")]
  public static void PrintDiagnostics() {
    Diagnostics(printToConsole: true);
  }
}
