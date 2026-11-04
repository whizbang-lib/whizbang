// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// The stream-integrity features the Dapper driver cannot serve, each with the configuration key that turns it on and the
/// reason. Stream integrity (checkpoints, gap detection, the audit and its digest epochs, the paced repair drain,
/// subscription-growth backfill and auto-repair) runs on the EF Core driver only; on Dapper every coordinator member it
/// calls is the interface's inert default.
/// </summary>
/// <remarks>
/// <para>
/// A silent no-op is the one outcome not allowed (#2051). So on a Dapper host each of these switches defaults to off
/// (<see cref="DapperStreamIntegrityDefaults"/>) rather than to the on that the EF Core driver ships, and a host whose
/// configuration turns one on is refused at start (<see cref="DapperStreamIntegrityRefusal"/>), naming the feature, the
/// key and the reason, instead of starting with a feature that does nothing.
/// </para>
/// <para>
/// The coordinator members behind them are named on the driver-parity allow-list with the same reason, so a member Dapper
/// later implements fails that test until its switch leaves this list.
/// </para>
/// </remarks>
/// <docs>data/drivers#what-dapper-refuses-at-startup</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamIntegrityRefusalTests.cs</tests>
internal static class DapperUnsupportedIntegrity {
  /// <summary>The configuration section the switches are bound from.</summary>
  internal const string SECTION = "Whizbang:StreamIntegrity";

  /// <summary>Why every switch here is refused on Dapper.</summary>
  internal const string REASON =
    "stream integrity runs on the EF Core driver only; on the Dapper driver its coordinator operations are not implemented, so the feature would do nothing";

  /// <summary>A switch: its key under <see cref="SECTION"/>, the feature it turns on, and how it is read and set.</summary>
  internal sealed record Switch(string Key, string Feature, Func<StreamIntegrityOptions, bool> IsOn, Action<StreamIntegrityOptions, bool> Set);

  /// <summary>Every boolean switch that turns on a stream-integrity feature.</summary>
  internal static readonly Switch[] SWITCHES = [
    new(nameof(StreamIntegrityOptions.CheckpointsEnabled), "stream integrity checkpoints",
      o => o.CheckpointsEnabled, (o, v) => o.CheckpointsEnabled = v),
    new(nameof(StreamIntegrityOptions.GapDetectionEnabled), "stream integrity gap detection",
      o => o.GapDetectionEnabled, (o, v) => o.GapDetectionEnabled = v),
    new(nameof(StreamIntegrityOptions.AuditEnabled), "the stream integrity audit",
      o => o.AuditEnabled, (o, v) => o.AuditEnabled = v),
    new(nameof(StreamIntegrityOptions.EpochClosureEnabled), "integrity digest epoch closure",
      o => o.EpochClosureEnabled, (o, v) => o.EpochClosureEnabled = v),
    new(nameof(StreamIntegrityOptions.RepairDrainEnabled), "the integrity repair drain",
      o => o.RepairDrainEnabled, (o, v) => o.RepairDrainEnabled = v),
    new(nameof(StreamIntegrityOptions.BackfillOnSubscriptionGrowth), "subscription-growth backfill (redelivery of a new type's history)",
      o => o.BackfillOnSubscriptionGrowth, (o, v) => o.BackfillOnSubscriptionGrowth = v),
  ];
}

/// <summary>
/// Turns every stream-integrity switch off on a Dapper host unless configuration sets it, so a host that configures
/// nothing starts with the features it can serve and none that would silently do nothing.
/// </summary>
/// <remarks>
/// Each switch takes the value configuration gives its key, else false. Configuration is read here as well as by the
/// worker pipeline's binding, so the outcome does not depend on which of the two is registered first: a key set to true
/// stays true, and <see cref="DapperStreamIntegrityRefusal"/> then refuses the host.
/// </remarks>
/// <param name="configuration">The host configuration, or null when the host registers none.</param>
/// <docs>data/drivers#what-dapper-refuses-at-startup</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamIntegrityRefusalTests.cs</tests>
internal sealed class DapperStreamIntegrityDefaults(IConfiguration? configuration) : IConfigureOptions<StreamIntegrityOptions> {
  /// <inheritdoc />
  public void Configure(StreamIntegrityOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    foreach (var @switch in DapperUnsupportedIntegrity.SWITCHES) {
      var configured = configuration?[$"{DapperUnsupportedIntegrity.SECTION}:{@switch.Key}"];
      @switch.Set(options, bool.TryParse(configured, out var on) && on);
    }
  }
}

/// <summary>
/// Refuses a Dapper host whose stream-integrity options turn on a feature the driver cannot serve, through the options
/// pipeline, so the host fails at start naming the feature, the configuration key and the reason.
/// </summary>
/// <docs>data/drivers#what-dapper-refuses-at-startup</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamIntegrityRefusalTests.cs</tests>
internal sealed class DapperStreamIntegrityRefusal : IValidateOptions<StreamIntegrityOptions> {
  /// <inheritdoc />
  public ValidateOptionsResult Validate(string? name, StreamIntegrityOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    List<string> failures = [];
    foreach (var @switch in DapperUnsupportedIntegrity.SWITCHES.Where(s => s.IsOn(options))) {
      failures.Add(_refusal(@switch.Feature, @switch.Key, "true", "false"));
    }
    if (options.RepairMode == IntegrityRepairMode.AutoRepairCapped) {
      failures.Add(_refusal("integrity auto-repair", nameof(StreamIntegrityOptions.RepairMode),
        nameof(IntegrityRepairMode.AutoRepairCapped), nameof(IntegrityRepairMode.ReportOnly)));
    }
    return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
  }

  private static string _refusal(string feature, string key, string value, string supported) =>
    $"{DapperUnsupportedIntegrity.SECTION}:{key} is {value}, which turns on {feature}, but the Dapper driver refuses it: "
    + $"{DapperUnsupportedIntegrity.REASON}. Set it to {supported}, or use the EF Core driver.";
}
