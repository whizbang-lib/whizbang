using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Generated;
using Whizbang.Core.Tests.Common;

namespace Whizbang.Core.Tests.Generated;

[Category("Diagnostics")]
public partial class GeneratedDiagnosticsTests : DiagnosticTestBase {

  [Test]
  public async Task Diagnostics_ShouldCollectReceptorDiscoveryInfoAsync() {
    // Arrange & Act
    var output = WhizbangDiagnostics.Diagnostics(
      categories: DiagnosticCategory.ReceptorDiscovery,
      printToConsole: false
    );

    // Assert
    await Assert.That(output).Contains("Whizbang Source Generators - Build Diagnostics");
    await Assert.That(output).Contains("ReceptorDiscoveryGenerator");
    await Assert.That(output).Contains("ReceptorDiscovery");
    await Assert.That(output).Contains($"Discovered {TestConstants.ExpectedReceptorCount} receptor(s)");
    await Assert.That(output).Contains("OrderReceptor");
    await Assert.That(output).Contains("PaymentReceptor");
    await Assert.That(output).Contains("(PaymentProcessed, AuditEvent)");
    await Assert.That(output).Contains("INotificationEvent[]");
  }

  [Test]
  public async Task Diagnostics_ShouldCaptureTimestampAsync() {
    // Arrange & Act
    var output = WhizbangDiagnostics.Diagnostics(
      categories: DiagnosticCategory.ReceptorDiscovery,
      printToConsole: false
    );

    // Assert - timestamp should be in UTC format
    await Assert.That(output).Contains(" UTC");
    await Assert.That(MyRegex().IsMatch(output)).IsTrue();
  }

  [Test]
  public async Task Diagnostics_ShouldDisplayFormattedOutputAsync() {
    // Arrange & Act
    var output = WhizbangDiagnostics.Diagnostics(
      categories: DiagnosticCategory.ReceptorDiscovery,
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
      categories: DiagnosticCategory.All,
      printToConsole: false
    );
    var receptorOnly = WhizbangDiagnostics.Diagnostics(
      categories: DiagnosticCategory.ReceptorDiscovery,
      printToConsole: false
    );

    // Assert
    await Assert.That(allOutput).Contains("ReceptorDiscovery");
    await Assert.That(receptorOnly).Contains("ReceptorDiscovery");

    // Both should contain receptor info
    await Assert.That(receptorOnly).Contains($"Discovered {TestConstants.ExpectedReceptorCount} receptor(s)");
  }

  [System.Text.RegularExpressions.GeneratedRegex(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} UTC"
  )]
  private static partial System.Text.RegularExpressions.Regex MyRegex();
}
