using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Targeted coverage for <see cref="PerspectiveRowRetentionConfigurator.StopAsync"/> — the
/// <see cref="Microsoft.Extensions.Hosting.IHostedService"/> half the broader
/// <see cref="PerspectiveRowRetentionConfiguratorTests"/> suite never calls (it only exercises
/// <c>StartAsync</c>).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRowRetentionConfigurator.cs</code-under-test>
[NotInParallel("PerspectiveTtlRegistryRuntimeConfig")]
public class PerspectiveRowRetentionConfiguratorCoverageTests {

  [Test]
  public async Task StopAsync_CompletesWithoutTouchingTheRegistryAsync() {
    // A hosted service that fails or hangs on shutdown blocks the rest of the host's shutdown
    // sequence. This step has nothing to release (the TTL registry is a static, process-lifetime
    // table it only ever wrote to at startup), so StopAsync must be an immediate no-op — never a
    // pending/incomplete task shutdown would have to wait on.
    var configurator = new PerspectiveRowRetentionConfigurator(
      Options.Create(new PerspectiveRowRetentionOptions()),
      NullLogger<PerspectiveRowRetentionConfigurator>.Instance);

    var stopTask = configurator.StopAsync(CancellationToken.None);

    await Assert.That(stopTask.IsCompletedSuccessfully).IsTrue()
      .Because("shutdown must never block on a step with nothing left to release");
    await stopTask;
  }
}
