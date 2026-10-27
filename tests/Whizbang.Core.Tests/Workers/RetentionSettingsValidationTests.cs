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
/// A negative retention window is rejected when the options are read, naming the configuration key.
/// </summary>
/// <remarks>
/// The sweep turns each window into <c>NOW() - n * INTERVAL</c>. A negative value puts the cutoff in the
/// future, so every row of that table becomes eligible at once: a typo in one setting would empty the
/// deduplication ledger, or every recovered dead letter, on the next cycle. Zero is allowed; it is a
/// deliberate "keep nothing past the current cycle".
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/RetentionSettingsBridge.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorkerOptionsValidator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Configuration/EphemeralOptionsValidator.cs</code-under-test>
[Category("Workers")]
public class RetentionSettingsValidationTests {

  [Test]
  [Arguments("DeduplicationDays")]
  [Arguments("StuckInboxDays")]
  [Arguments("RecoveredDeadLetterDays")]
  [Arguments("AbandonedStreamHours")]
  [Arguments("InstanceEvictionTombstoneHours")]
  [Arguments("OrphanPerspectiveEventGraceHours")]
  [Arguments("CollectiveOrderingHeadDays")]
  [Arguments("LogDays")]
  [Arguments("ScheduleRunDays")]
  [Arguments("StreamPurgeAuditDays")]
  [Arguments("IntegrityLedgerMaxEntries")]
  public async Task ANegativeMaintenanceRetention_IsRejectedByNameAsync(string property) {
    var retention = new MaintenanceRetentionOptions();
    switch (property) {
      case "DeduplicationDays": retention.DeduplicationDays = -1; break;
      case "StuckInboxDays": retention.StuckInboxDays = -1; break;
      case "RecoveredDeadLetterDays": retention.RecoveredDeadLetterDays = -1; break;
      case "AbandonedStreamHours": retention.AbandonedStreamHours = -1; break;
      case "InstanceEvictionTombstoneHours": retention.InstanceEvictionTombstoneHours = -1; break;
      case "OrphanPerspectiveEventGraceHours": retention.OrphanPerspectiveEventGraceHours = -1; break;
      case "LogDays": retention.LogDays = -1; break;
      case "ScheduleRunDays": retention.ScheduleRunDays = -1; break;
      case "StreamPurgeAuditDays": retention.StreamPurgeAuditDays = -1; break;
      case "IntegrityLedgerMaxEntries": retention.IntegrityLedgerMaxEntries = -1; break;
      default: retention.CollectiveOrderingHeadDays = -1; break;
    }

    var result = new MaintenanceWorkerOptionsValidator().Validate(null, new MaintenanceWorkerOptions { Retention = retention });

    await Assert.That(result.Failed).IsTrue()
      .Because("a negative window puts the cutoff in the future and makes every row of the table eligible at once");
    await Assert.That(result.FailureMessage).Contains($"Whizbang:Workers:Maintenance:Retention:{property}")
      .Because("the message names the key the operator has to fix, in the form they set it");
    await Assert.That(result.FailureMessage).Contains("-1");
  }

  [Test]
  [Arguments("RewindGraceSeconds", "Whizbang:Ephemeral:RewindGraceSeconds")]
  [Arguments("RetentionDays", "Whizbang:Ephemeral:PointerPrune:RetentionDays")]
  [Arguments("IntervalDays", "Whizbang:Ephemeral:PointerPrune:IntervalDays")]
  public async Task ANegativeEphemeralSetting_IsRejectedByNameAsync(string property, string key) {
    var ephemeral = new EphemeralOptions();
    switch (property) {
      case "RewindGraceSeconds": ephemeral.RewindGraceSeconds = -5; break;
      case "RetentionDays": ephemeral.PointerPrune.RetentionDays = -5; break;
      default: ephemeral.PointerPrune.IntervalDays = -5; break;
    }

    var result = new EphemeralOptionsValidator().Validate(null, ephemeral);

    await Assert.That(result.Failed).IsTrue();
    await Assert.That(result.FailureMessage).Contains(key);
  }

  [Test]
  public async Task SeveralNegativeValues_AreAllReportedAtOnceAsync() {
    var options = new MaintenanceWorkerOptions {
      Retention = new MaintenanceRetentionOptions { DeduplicationDays = -1, StuckInboxDays = -2 },
    };

    var result = new MaintenanceWorkerOptionsValidator().Validate(null, options);

    await Assert.That(result.Failures!.Count()).IsEqualTo(2)
      .Because("fixing one key at a time across restarts is how a misconfiguration costs an afternoon");
  }

  [Test]
  public async Task ZeroAndUnset_AreAcceptedAsync() {
    var options = new MaintenanceWorkerOptions {
      Retention = new MaintenanceRetentionOptions { DeduplicationDays = 0, AbandonedStreamHours = 0 },
    };
    var ephemeral = new EphemeralOptions {
      RewindGraceSeconds = 0,
      PointerPrune = new EphemeralPointerPruneOptions { Enabled = true, RetentionDays = 0, IntervalDays = 0 },
    };

    await Assert.That(new MaintenanceWorkerOptionsValidator().Validate(null, options).Succeeded).IsTrue()
      .Because("zero is a deliberate 'keep nothing past this cycle', and unset leaves the database value alone");
    await Assert.That(new MaintenanceWorkerOptionsValidator().Validate(null, new MaintenanceWorkerOptions()).Succeeded).IsTrue();
    await Assert.That(new EphemeralOptionsValidator().Validate(null, ephemeral).Succeeded).IsTrue();
    await Assert.That(new EphemeralOptionsValidator().Validate(null, new EphemeralOptions()).Succeeded).IsTrue();
  }

  [Test]
  public async Task AMissingNestedObject_IsRejectedByNameAsync() {
    var maintenance = new MaintenanceWorkerOptionsValidator().Validate(null, new MaintenanceWorkerOptions { Retention = null! });
    var ephemeral = new EphemeralOptionsValidator().Validate(null, new EphemeralOptions { PointerPrune = null! });

    await Assert.That(maintenance.Failed).IsTrue();
    await Assert.That(maintenance.FailureMessage).Contains("Whizbang:Workers:Maintenance:Retention");
    await Assert.That(ephemeral.Failed).IsTrue();
    await Assert.That(ephemeral.FailureMessage).Contains("Whizbang:Ephemeral:PointerPrune");
  }

  [Test]
  public async Task Validators_RejectANullOptionsInstanceAsync() {
    await Assert.That(() => new MaintenanceWorkerOptionsValidator().Validate(null, null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new EphemeralOptionsValidator().Validate(null, null!)).Throws<ArgumentNullException>();
  }

  private static ServiceProvider _hostWith(Dictionary<string, string?> settings) {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangWorkers();
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task TheTurnkeyPipeline_RejectsANegativeMaintenanceRetentionAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Workers:Maintenance:Retention:DeduplicationDays"] = "-3",
    });

    await Assert.That(() => provider.GetRequiredService<IOptions<MaintenanceWorkerOptions>>().Value)
      .Throws<OptionsValidationException>()
      .Because("the validator is registered by the pipeline, so a negative window fails where it is read, not in SQL");
  }

  [Test]
  public async Task TheTurnkeyPipeline_RejectsANegativeEphemeralSettingAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Ephemeral:PointerPrune:RetentionDays"] = "-3",
    });

    await Assert.That(() => provider.GetRequiredService<IOptions<EphemeralOptions>>().Value)
      .Throws<OptionsValidationException>();
  }
}
