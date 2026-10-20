// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The documented environment-variable form of each retention option reaches the option the bridge reads.
/// </summary>
/// <remarks>
/// <para>
/// No new binder call exists for these: the nested <c>Retention</c> object is reached by the existing
/// <c>Whizbang:Workers:Maintenance</c> bind, and <c>PointerPrune</c> by the existing <c>Whizbang:Ephemeral</c>
/// bind, both compiled by the configuration binder source generator. A class is settable when something reads
/// a section that reaches it, so this proves the reach end to end, through the provider that turns the
/// double-underscore names into sections.
/// </para>
/// <para>
/// Each test uses its own variable prefix, so the process-wide environment it writes cannot be read by any
/// other test, and removes what it set.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs</code-under-test>
[Category("Workers")]
public class RetentionSettingsBindingTests {

  private static readonly string[] _debugOnly = ["debug_mode"];

  private static ServiceProvider _hostFromEnvironment(string prefix) {
    var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangWorkers();
    return services.BuildServiceProvider();
  }

  private static async Task<T> _withEnvironmentAsync<T>(
      string prefix, Dictionary<string, string> variables, Func<ServiceProvider, T> read) {
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
  public async Task MaintenanceRetention_BindsFromItsEnvironmentVariablesAsync() {
    var retention = await _withEnvironmentAsync("WHZ1487A_", new Dictionary<string, string> {
      ["Whizbang__Workers__Maintenance__Retention__DeduplicationDays"] = "21",
      ["Whizbang__Workers__Maintenance__Retention__StuckInboxDays"] = "22",
      ["Whizbang__Workers__Maintenance__Retention__RecoveredDeadLetterDays"] = "23",
      ["Whizbang__Workers__Maintenance__Retention__AbandonedStreamHours"] = "24",
      ["Whizbang__Workers__Maintenance__Retention__InstanceEvictionTombstoneHours"] = "25",
      ["Whizbang__Workers__Maintenance__Retention__OrphanPerspectiveEventGraceHours"] = "26",
      ["Whizbang__Workers__Maintenance__Retention__CollectiveOrderingHeadDays"] = "27",
    }, p => p.GetRequiredService<IOptions<MaintenanceWorkerOptions>>().Value.Retention);

    await Assert.That(retention.DeduplicationDays).IsEqualTo(21)
      .Because("a documented environment variable that nothing reads looks authoritative and fails silently");
    await Assert.That(retention.StuckInboxDays).IsEqualTo(22);
    await Assert.That(retention.RecoveredDeadLetterDays).IsEqualTo(23);
    await Assert.That(retention.AbandonedStreamHours).IsEqualTo(24);
    await Assert.That(retention.InstanceEvictionTombstoneHours).IsEqualTo(25);
    await Assert.That(retention.OrphanPerspectiveEventGraceHours).IsEqualTo(26);
    await Assert.That(retention.CollectiveOrderingHeadDays).IsEqualTo(27);
  }

  [Test]
  public async Task EphemeralRetention_BindsFromItsEnvironmentVariablesAsync() {
    var ephemeral = await _withEnvironmentAsync("WHZ1487B_", new Dictionary<string, string> {
      ["Whizbang__Ephemeral__RewindGraceSeconds"] = "31",
      ["Whizbang__Ephemeral__PointerPrune__Enabled"] = "true",
      ["Whizbang__Ephemeral__PointerPrune__RetentionDays"] = "32",
      ["Whizbang__Ephemeral__PointerPrune__IntervalDays"] = "33",
    }, p => p.GetRequiredService<IOptions<EphemeralOptions>>().Value);

    await Assert.That(ephemeral.RewindGraceSeconds).IsEqualTo(31);
    await Assert.That(ephemeral.PointerPrune.Enabled).IsEqualTo(true);
    await Assert.That(ephemeral.PointerPrune.RetentionDays).IsEqualTo(32);
    await Assert.That(ephemeral.PointerPrune.IntervalDays).IsEqualTo(33);
  }

  [Test]
  public async Task NothingConfigured_LeavesEveryRetentionOptionUnsetAsync() {
    var (retention, ephemeral) = await _withEnvironmentAsync("WHZ1487C_", [],
      p => (p.GetRequiredService<IOptions<MaintenanceWorkerOptions>>().Value.Retention,
            p.GetRequiredService<IOptions<EphemeralOptions>>().Value));

    await Assert.That(RetentionSettingsBridge.SettingsFor(false, retention, ephemeral).Select(s => s.Key).ToArray())
      .IsEquivalentTo(_debugOnly)
      .Because("binding must not invent values: an unset option leaves the stored value, possibly set in SQL, alone");
  }
}
