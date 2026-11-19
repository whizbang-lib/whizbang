// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Archival;

/// <summary>
/// The hosted startup step that applies <c>Whizbang:Perspectives:ArchivedStreamRows</c> to the process-wide policy
/// registry (#1739), so the generated runner's probe for a missing row sees the configured policy, not only the
/// attribute.
/// </summary>
/// <docs>fundamentals/events/archive-store#perspective-rows</docs>
/// <code-under-test>src/Whizbang.Core/Archival/ArchivedStreamRowsConfigurator.cs</code-under-test>
[NotInParallel("ArchivedStreamRowPoliciesRuntimeConfig")]
public class ArchivedStreamRowsConfiguratorTests {
  [Test]
  public async Task StartAsync_AppliesTheConfiguredOverrides_AndStopAsyncDoesNothingAsync() {
    var name = $"Tests.ArchivedRows.Configured+{Guid.NewGuid():N}";
    var options = new ArchivedStreamRowsOptions();
    options.Perspectives[name] = ArchivedStreamRowPolicy.Drop;
    var configurator = new ArchivedStreamRowsConfigurator(Options.Create(options));
    try {
      await configurator.StartAsync(CancellationToken.None);

      await Assert.That(ArchivedStreamRowPolicies.Resolve(name)).IsEqualTo(ArchivedStreamRowPolicy.Drop)
        .Because("startup applies the operator's policy before any perspective runs");

      await configurator.StopAsync(CancellationToken.None);

      await Assert.That(ArchivedStreamRowPolicies.Resolve(name)).IsEqualTo(ArchivedStreamRowPolicy.Drop);
    } finally {
      ArchivedStreamRowPolicies.ApplyRuntimeConfiguration(null);
    }
  }

  [Test]
  public async Task Constructor_RefusesNullOptionsAsync() {
    await Assert.That(() => new ArchivedStreamRowsConfigurator(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task TheWorkerPipeline_RegistersTheConfiguratorAsAHostedService_OnceAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangWorkers();
    services.AddWhizbangWorkers();

    var registered = services
      .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ArchivedStreamRowsConfigurator))
      .ToList();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(registered).Count().IsEqualTo(1)
      .Because("the overrides must be applied on every host that runs perspectives, once");
    await Assert.That(ActivatorUtilities.CreateInstance<ArchivedStreamRowsConfigurator>(provider)).IsNotNull()
      .Because("its one dependency, the bound options, is registered by the same pipeline");
  }
}
