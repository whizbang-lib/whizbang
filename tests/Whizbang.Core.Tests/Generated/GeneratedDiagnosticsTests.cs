using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Generated;
using Whizbang.Core.Tests.Common;

namespace Whizbang.Core.Tests.Generated;

[Category("Diagnostics")]
public class GeneratedDiagnosticsTests : DiagnosticTestBase {

  [Test]
  public async Task Diagnostics_ShouldCollectReceptorDiscoveryInfo() {
    // Arrange & Act
    var output = WhizbangDiagnostics.Diagnostics(
      categories: DiagnosticCategory.ReceptorDiscovery,
      printToConsole: false
    );

    // Assert
    await Assert.That(output).Contains("Whizbang Source Generators - Build Diagnostics");
    await Assert.That(output).Contains("ReceptorDiscoveryGenerator");
    await Assert.That(output).Contains("ReceptorDiscovery");
    await Assert.That(output).Contains("Discovered 5 receptor(s)");
    await Assert.That(output).Contains("OrderReceptor");
    await Assert.That(output).Contains("PaymentReceptor");
    await Assert.That(output).Contains("(PaymentProcessed, AuditEvent)");
    await Assert.That(output).Contains("INotificationEvent[]");
  }

  [Test]
  public async Task Diagnostics_ShouldCaptureTimestamp() {
    // Arrange & Act
    var output = WhizbangDiagnostics.Diagnostics(
      categories: DiagnosticCategory.ReceptorDiscovery,
      printToConsole: false
    );

    // Assert - timestamp should be in UTC format
    await Assert.That(output).Contains(" UTC");
    await Assert.That(System.Text.RegularExpressions.Regex.IsMatch(
        output,
        @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} UTC"
    )).IsTrue();
  }

  [Test]
  public async Task Diagnostics_ShouldDisplayFormattedOutput() {
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
  public async Task Diagnostics_ShouldFilterByCategory() {
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
    await Assert.That(receptorOnly).Contains("Discovered 5 receptor(s)");
  }
}
