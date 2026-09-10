using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The gate the worker pipeline registers is built from <see cref="WorkCoordinatorGateOptions"/>,
/// bound from <c>Whizbang:WorkCoordinatorGate</c> and open to a driver's post-configuration, so the
/// documented cap on concurrent coordinator calls is the one that takes effect. It used to be a
/// literal 50: <c>PostgresOptions.MaxInFlightCommands</c> reached nothing.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs</code-under-test>
public class WorkCoordinatorGateRegistrationTests {
  private static ServiceCollection _services(IConfiguration? configuration = null) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton(configuration ?? new ConfigurationBuilder().Build());
    return services;
  }

  [Test]
  public async Task AddWhizbangWorkers_BindsTheGateFromConfigurationAsync() {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:WorkCoordinatorGate:MaxConcurrent"] = "7",
      ["Whizbang:WorkCoordinatorGate:AcquireTimeoutMilliseconds"] = "1234",
    }).Build();
    var services = _services(configuration);
    services.AddWhizbangWorkers();

    await using var provider = services.BuildServiceProvider();
    var gate = provider.GetRequiredService<WorkCoordinatorGate>();

    await Assert.That(gate.MaxConcurrent).IsEqualTo(7)
      .Because("the cap is configuration, not a literal in the pipeline");
    await Assert.That(gate.AcquireTimeoutMilliseconds).IsEqualTo(1234);
  }

  [Test]
  public async Task AddWhizbangWorkers_BindsTheInteractiveReserveFromConfigurationAsync() {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:WorkCoordinatorGate:MaxConcurrent"] = "10",
      ["Whizbang:WorkCoordinatorGate:InteractiveReserve"] = "3",
    }).Build();
    var services = _services(configuration);
    services.AddWhizbangWorkers();

    await using var provider = services.BuildServiceProvider();
    var gate = provider.GetRequiredService<WorkCoordinatorGate>();

    await Assert.That(gate.InteractiveReserve).IsEqualTo(3)
      .Because("the documented option reaches the gate; the default of one tenth (here 1) would be a silent no-op");
  }

  [Test]
  public async Task AddWhizbangWorkers_WithoutAnInteractiveReserve_HoldsOneTenthBackAsync() {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:WorkCoordinatorGate:MaxConcurrent"] = "25",
    }).Build();
    var services = _services(configuration);
    services.AddWhizbangWorkers();

    await using var provider = services.BuildServiceProvider();
    var gate = provider.GetRequiredService<WorkCoordinatorGate>();

    await Assert.That(gate.InteractiveReserve).IsEqualTo(2)
      .Because("one tenth of 25 rounded down; the reserve is adaptive to the cap, not a second knob to tune");
  }

  [Test]
  public async Task AddWhizbangWorkers_WithInteractiveReserveZero_DisablesTheReserveAsync() {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:WorkCoordinatorGate:MaxConcurrent"] = "10",
      ["Whizbang:WorkCoordinatorGate:InteractiveReserve"] = "0",
    }).Build();
    var services = _services(configuration);
    services.AddWhizbangWorkers();

    await using var provider = services.BuildServiceProvider();
    var gate = provider.GetRequiredService<WorkCoordinatorGate>();

    await Assert.That(gate.InteractiveReserve).IsEqualTo(0)
      .Because("zero is the operator's explicit word, not 'unset'");
  }

  [Test]
  public async Task AddWhizbangWorkers_WithoutConfiguration_KeepsTheDefaultsAsync() {
    var services = _services();
    services.AddWhizbangWorkers();

    await using var provider = services.BuildServiceProvider();
    var gate = provider.GetRequiredService<WorkCoordinatorGate>();

    await Assert.That(gate.MaxConcurrent).IsEqualTo(50);
    await Assert.That(gate.AcquireTimeoutMilliseconds).IsEqualTo(30_000);
  }

  [Test]
  public async Task ADriverPostConfiguration_ReachesTheGateAsync() {
    var services = _services();
    services.AddWhizbangWorkers();
    // What a data driver does with its own documented option (PostgresOptions.MaxInFlightCommands).
    services.PostConfigure<WorkCoordinatorGateOptions>(o => o.MaxConcurrent = 9);

    await using var provider = services.BuildServiceProvider();
    var gate = provider.GetRequiredService<WorkCoordinatorGate>();

    await Assert.That(gate.MaxConcurrent).IsEqualTo(9)
      .Because("a driver's cap is applied after configuration binding, whichever registration ran first");
  }

  [Test]
  public async Task AGateRegisteredBeforeThePipeline_IsKeptAsync() {
    var services = _services();
    services.AddSingleton(new WorkCoordinatorGate(maxConcurrent: 3));
    services.AddWhizbangWorkers();

    await using var provider = services.BuildServiceProvider();
    var gate = provider.GetRequiredService<WorkCoordinatorGate>();

    await Assert.That(gate.MaxConcurrent).IsEqualTo(3)
      .Because("a consumer's own gate still wins; the pipeline only fills the gap");
  }
}
