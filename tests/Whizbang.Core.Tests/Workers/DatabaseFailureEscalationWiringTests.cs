// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Issue #1986: the configured escalation threshold reaches every worker loop that reports a transient database
/// failure, as the turnkey pipeline builds it. The workers are type-activated and take the options as an optional
/// parameter, so a registration that forgot the plain instance would leave each of them on the defaults with nothing
/// to show for it.
/// </summary>
/// <docs>fundamentals/workers/transient-database-failures#when-the-database-keeps-failing</docs>
[Category("Workers")]
public sealed class DatabaseFailureEscalationWiringTests {
  private static ServiceProvider _host() {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:DatabaseFailureEscalation:ConsecutiveFailures"] = "9",
        ["Whizbang:DatabaseFailureEscalation:FailingFor"] = "00:07:00",
      })
      .Build();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddLogging();
    // What a host's own registrations (AddWhizbang, the generated registries) supply and the worker pipeline does not.
    services.AddSingleton(new JsonSerializerOptions());
    services.AddSingleton<IPerspectiveSyncSignaler>(new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance));
    services.AddSingleton<ISyncEventTracker>(new SyncEventTracker());
    services.AddSingleton<ITimeProvider, SystemTimeProvider>();
    services.AddWhizbangWorkers();
    return services.BuildServiceProvider();
  }

  private static async Task _assertConfiguredAsync(DatabaseFailureEscalation escalation) {
    await Assert.That(escalation.Options.ConsecutiveFailures).IsEqualTo(9)
      .Because("the worker is built by the container, which must hand it the bound option or the setting does nothing");
    await Assert.That(escalation.Options.FailingFor).IsEqualTo(TimeSpan.FromMinutes(7));
  }

  [Test]
  public async Task ClaimWorker_TakesTheConfiguredThresholdAsync() {
    await using var provider = _host();

    await _assertConfiguredAsync(provider.GetRequiredService<ClaimWorker>().DatabaseFailures);
  }

  [Test]
  public async Task InboxDrainWorker_TakesTheConfiguredThresholdAsync() {
    await using var provider = _host();

    await _assertConfiguredAsync(provider.GetRequiredService<InboxDrainWorker>().DatabaseFailures);
  }

  [Test]
  public async Task OutboxDrainWorker_TakesTheConfiguredThresholdAsync() {
    await using var provider = _host();

    await _assertConfiguredAsync(provider.GetRequiredService<OutboxDrainWorker>().DatabaseFailures);
  }

  [Test]
  public async Task PerspectiveWorker_TakesTheConfiguredThresholdAsync() {
    await using var provider = _host();

    await _assertConfiguredAsync(provider.GetRequiredService<PerspectiveWorker>().DatabaseFailures);
  }
}
