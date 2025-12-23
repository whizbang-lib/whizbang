using System.Collections.Concurrent;
using Microsoft.Testing.Platform.Extensions;
using Microsoft.Testing.Platform.Messages;
using Microsoft.Testing.Platform.OutputDevice;
using Microsoft.Testing.Platform.Services;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests.Common;

/// <summary>
/// Base class for tests that want to display build diagnostics on demand.
/// Diagnostics are shown:
/// - Once before the first test in the class (if enabled via env var or CLI param)
/// - After each failing test (if enabled via env var or CLI param)
///
/// To enable diagnostics, use either:
/// - Environment variable: WHIZBANG_SHOW_DIAGNOSTICS=1
/// - Command-line parameter: dotnet test -- --test-parameter ShowDiagnostics=true
/// </summary>
public class DiagnosticTestBase {
  private static readonly ConcurrentDictionary<Type, bool> _diagnosticsPrintedByClass = new();

  /// <summary>
  /// Override this property to specify which diagnostic categories to display.
  /// Default is ReceptorDiscovery.
  /// </summary>
  protected virtual DiagnosticCategory DiagnosticCategories => DiagnosticCategory.ReceptorDiscovery;

  [Before(Class)]
  public static void ClassSetup() {
    // Register diagnostics once before all tests in this class
    ReceptorDiscoveryDiagnostics.Register();
  }

  [Before(Test)]
  public virtual async Task TestSetupAsync() {
    // Always write diagnostics to StandardOutput at the START of each test
    // This ensures it's captured before the test result is published
    if (_isDiagnosticsEnabled()) {
      var diagnostics = WhizbangDiagnostics.Diagnostics(
          DiagnosticCategories,
          printToConsole: false
      );

      var diagnosticMessage = $"\n{new string('=', 70)}\nBUILD DIAGNOSTICS:\n{new string('=', 70)}\n{diagnostics}\n{new string('=', 70)}\n";

      // Write to StandardOutput BEFORE the test runs
      await TestContext.Current!.Output.StandardOutput.WriteLineAsync(diagnosticMessage);
    }
  }


  private static bool _isDiagnosticsEnabled() {
    var showViaEnv = Environment.GetEnvironmentVariable("WHIZBANG_SHOW_DIAGNOSTICS") == "1";
    var showViaParam = TestContext.Parameters.TryGetValue("ShowDiagnostics", out var values)
        && values.Contains("true");

    return showViaEnv || showViaParam;
  }
}
