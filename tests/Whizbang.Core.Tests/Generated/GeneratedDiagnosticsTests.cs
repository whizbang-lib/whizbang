using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Generated;
using Whizbang.Core.Tests.Common;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests.Generated;

[Category("Diagnostics")]
public partial class GeneratedDiagnosticsTests : DiagnosticTestBase {

  [Test]
  public async Task Diagnostics_ShouldCollectReceptorDiscoveryInfoAsync() {
    // Arrange & Act
    var output = WhizbangDiagnostics.Diagnostics(
      categories: DiagnosticCategories.ReceptorDiscovery,
      printToConsole: false
    );

    // Assert
    await Assert.That(output).Contains("Whizbang Source Generators - Build Diagnostics");
    await Assert.That(output).Contains("ReceptorDiscoveryGenerator");
    await Assert.That(output).Contains("ReceptorDiscovery");
    await Assert.That(output).Contains($"Discovered {TestConstants.EXPECTED_RECEPTOR_COUNT} receptor(s)");
    await Assert.That(output).Contains("OrderReceptor");
    await Assert.That(output).Contains("PaymentReceptor");
    await Assert.That(output).Contains("(PaymentProcessed, AuditEvent)");
    await Assert.That(output).Contains("INotificationEvent[]");
  }

  [Test]
  public async Task Diagnostics_ShouldCaptureDeterministicBuildStampAsync() {
    // Arrange & Act
    var output = WhizbangDiagnostics.Diagnostics(
      categories: DiagnosticCategories.ReceptorDiscovery,
      printToConsole: false
    );

    // Assert - the stamp is the generator's build version, never wall-clock time:
    // wall-clock text baked into generated source changes the compiler's
    // deterministic output hash (MVID + PDB id) on every build, which breaks the
    // byte-identical rebuild verification CI performs (verify-rebuild in ci.yml).
    await Assert.That(output).Contains("generator v");
    await Assert.That(WallClockRegex().IsMatch(output)).IsFalse();
  }

  [Test]
  public async Task Diagnostics_ShouldDisplayFormattedOutputAsync() {
    // Arrange & Act
    var output = WhizbangDiagnostics.Diagnostics(
      categories: DiagnosticCategories.ReceptorDiscovery,
      printToConsole: false
    );
    var lines = output.Split(Environment.NewLine);

    // Assert - check for proper formatting
    await Assert.That(lines.Any(line => line.Contains("═══════════════"))).IsTrue();
    await Assert.That(lines.Any(line => line.Contains("───────────────"))).IsTrue();
    await Assert.That(lines.Any(line => line.Contains("Total Generators: 1"))).IsTrue();
  }

  [Test]
  public async Task Diagnostics_ShouldFilterByCategoryAsync() {
    // Arrange & Act
    var allOutput = WhizbangDiagnostics.Diagnostics(
      categories: DiagnosticCategories.All,
      printToConsole: false
    );
    var receptorOnly = WhizbangDiagnostics.Diagnostics(
      categories: DiagnosticCategories.ReceptorDiscovery,
      printToConsole: false
    );

    // Assert
    await Assert.That(allOutput).Contains("ReceptorDiscovery");
    await Assert.That(receptorOnly).Contains("ReceptorDiscovery");

    // Both should contain receptor info
    await Assert.That(receptorOnly).Contains($"Discovered {TestConstants.EXPECTED_RECEPTOR_COUNT} receptor(s)");
  }

  [System.Text.RegularExpressions.GeneratedRegex(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} UTC"
  )]
  private static partial System.Text.RegularExpressions.Regex WallClockRegex();
}
