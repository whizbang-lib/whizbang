// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Issue #1611: the partition manager's options bind from <c>Whizbang:Workers:Maintenance:Partitioning</c>, default to
/// off with three intervals ahead, and refuse an ahead count below one where the options are read.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenancePartitioningOptions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorkerOptionsValidator.cs</code-under-test>
[Category("Workers")]
public class PartitioningOptionsTests {

  private static ServiceProvider _hostFromEnvironment(string prefix) {
    var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangWorkers();
    return services.BuildServiceProvider();
  }

  private static async Task<T> _withEnvironmentAsync<T>(string prefix, Dictionary<string, string> variables, Func<ServiceProvider, T> read) {
    foreach (var (name, value) in variables) {
      Environment.SetEnvironmentVariable(prefix + name, value);
    }
    try {
      await using var provider = _hostFromEnvironment(prefix);
      return read(provider);
    } finally {
      foreach (var name in variables.Keys) {
        Environment.SetEnvironmentVariable(prefix + name, null);
      }
    }
  }

  [Test]
  public async Task Defaults_AreOff_WithThreeIntervalsAheadAsync() {
    var options = new MaintenanceWorkerOptions();

    await Assert.That(options.Partitioning.Enabled).IsFalse()
      .Because("partitioning stays off until the conversion has soaked");
    await Assert.That(options.Partitioning.CreateAhead).IsEqualTo(3);
  }

  [Test]
  public async Task Partitioning_BindsFromItsEnvironmentVariablesAsync() {
    var partitioning = await _withEnvironmentAsync("WHZ1611A_", new Dictionary<string, string> {
      ["Whizbang__Workers__Maintenance__Partitioning__Enabled"] = "true",
      ["Whizbang__Workers__Maintenance__Partitioning__CreateAhead"] = "5",
    }, p => p.GetRequiredService<IOptions<MaintenanceWorkerOptions>>().Value.Partitioning);

    await Assert.That(partitioning.Enabled).IsTrue();
    await Assert.That(partitioning.CreateAhead).IsEqualTo(5);
  }

  [Test]
  public async Task CreateAheadBelowOne_IsRejectedByNameAsync() {
    var message = await _withEnvironmentAsync("WHZ1611B_", new Dictionary<string, string> {
      ["Whizbang__Workers__Maintenance__Partitioning__CreateAhead"] = "0",
    }, p => {
      try {
        _ = p.GetRequiredService<IOptions<MaintenanceWorkerOptions>>().Value;
        return "read without a validation failure";
      } catch (OptionsValidationException ex) {
        return ex.Message;
      }
    });

    await Assert.That(message).Contains("Whizbang:Workers:Maintenance:Partitioning:CreateAhead must be 1 or greater (was 0)");
  }

  [Test]
  public async Task NullPartitioning_IsRejectedAsync() {
    var result = new MaintenanceWorkerOptionsValidator().Validate(null, new MaintenanceWorkerOptions { Partitioning = null! });

    await Assert.That(result.Failed).IsTrue();
    await Assert.That(result.FailureMessage).Contains("Whizbang:Workers:Maintenance:Partitioning must not be null.");
  }

  [Test]
  public async Task ValidOptions_PassAsync() {
    var result = new MaintenanceWorkerOptionsValidator().Validate(
      null, new MaintenanceWorkerOptions { Partitioning = new MaintenancePartitioningOptions { Enabled = true, CreateAhead = 1 } });

    await Assert.That(result.Succeeded).IsTrue();
  }
}
