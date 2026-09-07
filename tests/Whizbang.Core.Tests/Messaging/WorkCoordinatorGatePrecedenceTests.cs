using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The <c>Whizbang:WorkCoordinatorGate</c> section is the operator's explicit word on the gate's cap. A
/// data driver's own documented option (<c>PostgresOptions.MaxInFlightCommands</c>) only fills the gap when
/// the section is silent, and the default applies when both are. A driver that overwrote the section made
/// the section inert on every deployment with a Postgres driver, which is every deployment.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/WorkCoordinatorGateOptions.cs</code-under-test>
public class WorkCoordinatorGatePrecedenceTests {
  private static ServiceCollection _services(IConfiguration? configuration = null) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton(configuration ?? new ConfigurationBuilder().Build());
    return services;
  }

  private static IConfiguration _section(int maxConcurrent) =>
    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:WorkCoordinatorGate:MaxConcurrent"] = maxConcurrent.ToString(System.Globalization.CultureInfo.InvariantCulture),
    }).Build();

  [Test]
  public async Task ASectionValue_WinsOverADriverThatFillsTheGapAsync() {
    var services = _services(_section(7));
    services.AddWhizbangWorkers();
    // What a data driver does with its own documented option: fill the gap, never overwrite.
    services.PostConfigure<WorkCoordinatorGateOptions>(o => o.MaxConcurrent ??= 9);

    await using var provider = services.BuildServiceProvider();
    var gate = provider.GetRequiredService<WorkCoordinatorGate>();

    await Assert.That(gate.MaxConcurrent).IsEqualTo(7)
      .Because("the section is the operator's explicit word; a driver's default must not overwrite it");
  }

  [Test]
  public async Task ADriverFillsTheGap_WhenTheSectionIsSilentAsync() {
    var services = _services();
    services.AddWhizbangWorkers();
    services.PostConfigure<WorkCoordinatorGateOptions>(o => o.MaxConcurrent ??= 9);

    await using var provider = services.BuildServiceProvider();
    var gate = provider.GetRequiredService<WorkCoordinatorGate>();

    await Assert.That(gate.MaxConcurrent).IsEqualTo(9)
      .Because("with the section silent, the driver's documented option is the cap");
  }

  [Test]
  public async Task NothingConfigured_UsesTheDefaultAsync() {
    var services = _services();
    services.AddWhizbangWorkers();

    await using var provider = services.BuildServiceProvider();
    var gate = provider.GetRequiredService<WorkCoordinatorGate>();

    await Assert.That(gate.MaxConcurrent).IsEqualTo(WorkCoordinatorGateOptions.DefaultMaxConcurrent);
    await Assert.That(WorkCoordinatorGateOptions.DefaultMaxConcurrent).IsEqualTo(50)
      .Because("the default the pipeline has always used, now named");
  }
}
