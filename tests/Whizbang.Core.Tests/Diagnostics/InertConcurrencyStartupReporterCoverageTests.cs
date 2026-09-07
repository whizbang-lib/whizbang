using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;

namespace Whizbang.Core.Tests.Diagnostics;

/// <summary>
/// Covers <see cref="InertConcurrencyStartupReporter.StopAsync"/> — untouched by the sibling
/// <c>InertConcurrencyStartupReporterTests</c>, which only ever calls <c>StartAsync</c>.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Diagnostics/InertConcurrencyStartupReporter.cs</code-under-test>
[Category("Diagnostics")]
public class InertConcurrencyStartupReporterCoverageTests {

  [Test]
  public async Task StopAsync_CompletesWithoutThrowing_EvenWithAnAlreadyCanceledTokenAsync() {
    // IHostedService.StopAsync runs during host shutdown, which frequently passes an
    // already-canceled token (a shutdown deadline that expired). This reporter has nothing to
    // clean up, but if it ever grew a real stop path that observed the token, throwing here
    // would turn a harmless startup diagnostic into a failure that blocks graceful shutdown.
    var reporter = new InertConcurrencyStartupReporter(
      NullLogger<InertConcurrencyStartupReporter>.Instance,
      services: null);
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    await Assert.That(async () => await reporter.StopAsync(cts.Token)).ThrowsNothing();
  }
}
