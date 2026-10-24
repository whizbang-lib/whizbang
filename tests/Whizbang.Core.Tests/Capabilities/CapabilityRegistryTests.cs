// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Observability;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Capabilities;

/// <summary>
/// The registry aggregates every provider's capabilities across each provider's scopes, answers
/// Unknown until probed, and turns a provider that cannot probe a scope into Unknown with a warning,
/// without touching any other provider.
/// </summary>
/// <docs>operations/infrastructure/capabilities</docs>
public class CapabilityRegistryTests {
  private static readonly CapabilityId _vector = new("postgres", "vector");
  private static readonly CapabilityId _delayed = new("broker", "delayed_exchange");

  private static FakeCapabilityProvider _database() => new("postgres", ["orders-db", "audit-db"],
    FakeCapabilityProvider.Describe("postgres", "vector"), FakeCapabilityProvider.Describe("postgres", "pg_squeeze"));

  private static FakeCapabilityProvider _broker() => new("broker", ["vhost-a"],
    FakeCapabilityProvider.Describe("broker", "delayed_exchange"));

  [Test]
  public async Task BeforeTheFirstProbe_EveryCapabilityInEveryScopeReadsUnknownAsync() {
    var registry = new CapabilityRegistry([_database(), _broker()], NullLogger<CapabilityRegistry>.Instance);

    await Assert.That(registry.Statuses.Count).IsEqualTo(5);
    await Assert.That(registry.Statuses.All(s => s.State == CapabilityState.Unknown && s.Reason == CapabilityRegistry.NOT_PROBED)).IsTrue();
    await Assert.That(registry.Providers.Count).IsEqualTo(2);
  }

  [Test]
  public async Task RefreshAsync_AggregatesEveryProviderAndScope_WhateverTheProviderAsync() {
    var database = _database();
    database.States["pg_squeeze"] = CapabilityState.NotPermitted;
    var broker = _broker();
    broker.States["delayed_exchange"] = CapabilityState.NeedsServerChange;
    var registry = new CapabilityRegistry([database, broker], NullLogger<CapabilityRegistry>.Instance);

    await registry.RefreshAsync(CancellationToken.None);

    await Assert.That(registry.Statuses.Count).IsEqualTo(5);
    await Assert.That(registry.GetState(_vector, "orders-db")).IsEqualTo(CapabilityState.Ready);
    await Assert.That(registry.GetState(new CapabilityId("postgres", "pg_squeeze"), "audit-db")).IsEqualTo(CapabilityState.NotPermitted);
    await Assert.That(registry.GetState(_delayed, "vhost-a")).IsEqualTo(CapabilityState.NeedsServerChange);
    await Assert.That(database.Probes).IsEqualTo(2);
    await Assert.That(broker.Probes).IsEqualTo(1);
  }

  [Test]
  public async Task GetState_ComparesProviderAndNameWithoutRegardToCaseAsync() {
    var registry = new CapabilityRegistry([_database()], NullLogger<CapabilityRegistry>.Instance);
    await registry.RefreshAsync(CancellationToken.None);

    await Assert.That(registry.GetState(new CapabilityId("Postgres", "VECTOR"), "orders-db")).IsEqualTo(CapabilityState.Ready);
  }

  [Test]
  [Arguments("other", "vector", "orders-db")]
  [Arguments("postgres", "not_a_capability", "orders-db")]
  [Arguments("postgres", "vector", "other-db")]
  public async Task GetState_AnythingUnregistered_IsUnknownAsync(string provider, string name, string scope) {
    var registry = new CapabilityRegistry([_database()], NullLogger<CapabilityRegistry>.Instance);
    await registry.RefreshAsync(CancellationToken.None);

    await Assert.That(registry.GetState(new CapabilityId(provider, name), scope)).IsEqualTo(CapabilityState.Unknown);
  }

  [Test]
  public async Task AProviderThatCannotProbe_ReadsUnknownAndWarns_WithoutAffectingOthersAsync() {
    var database = _database();
    database.Failure = new InvalidOperationException("no route to host");
    var logger = new FakeLogger<CapabilityRegistry>();
    var registry = new CapabilityRegistry([database, _broker()], logger);

    await registry.RefreshAsync(CancellationToken.None);

    await Assert.That(registry.GetState(_vector, "orders-db")).IsEqualTo(CapabilityState.Unknown);
    await Assert.That(registry.Statuses.Single(s => s.Descriptor.Id == _vector && s.Scope == "orders-db").Reason)
      .Contains("no route to host");
    await Assert.That(registry.GetState(_delayed, "vhost-a")).IsEqualTo(CapabilityState.Ready);
    var warnings = logger.Collector.GetSnapshot().Where(r => r.Level == LogLevel.Warning).ToArray();
    await Assert.That(warnings.Length).IsEqualTo(2).Because("one warning per scope the provider could not probe");
    await Assert.That(warnings[0].Message).Contains("postgres");
    await Assert.That(warnings[0].Message).Contains("orders-db");
    await Assert.That(warnings[0].Message).Contains("no route to host");
  }

  [Test]
  public async Task ATimeoutThatIsNotTheCallersCancellation_IsUnknownAsync() {
    var broker = _broker();
    broker.Failure = new OperationCanceledException("command timed out");
    var registry = new CapabilityRegistry([broker], NullLogger<CapabilityRegistry>.Instance);

    await registry.RefreshAsync(CancellationToken.None);

    await Assert.That(registry.Statuses.Single().Reason).Contains("command timed out");
  }

  [Test]
  public async Task TheCallersCancellation_PropagatesAsync() {
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();
    var broker = _broker();
    broker.Failure = new OperationCanceledException(cts.Token);
    var registry = new CapabilityRegistry([broker], NullLogger<CapabilityRegistry>.Instance);

    await Assert.That(async () => await registry.RefreshAsync(cts.Token)).Throws<OperationCanceledException>()
      .Because("host shutdown is not a probe failure");
  }

  [Test]
  public async Task WithMetrics_TheGaugeIsCreated_WithoutNoneIsAsync() {
    await using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var metrics = new WhizbangMetrics(provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());

    var withMetrics = new CapabilityRegistry([_broker()], NullLogger<CapabilityRegistry>.Instance, metrics);
    var without = new CapabilityRegistry([_broker()], NullLogger<CapabilityRegistry>.Instance);

    await Assert.That(withMetrics.Metrics).IsNotNull();
    await Assert.That(GaugeRecorder.Collect(withMetrics.Metrics!.CapabilityState).Count).IsEqualTo(1);
    await Assert.That(without.Metrics).IsNull();
  }

  [Test]
  public async Task Verdict_IsEmptyUntilPublished_ThenTheLatestAsync() {
    var registry = new CapabilityRegistry([_broker()], NullLogger<CapabilityRegistry>.Instance);
    var verdict = CapabilityVerdict.Evaluate(registry.Statuses, []);

    var before = registry.Verdict;
    registry.PublishVerdict(verdict);

    await Assert.That(before).IsSameReferenceAs(CapabilityVerdict.Empty);
    await Assert.That(registry.Verdict).IsSameReferenceAs(verdict);
    await Assert.That(await registry.WaitForVerdictAsync(CancellationToken.None)).IsSameReferenceAs(verdict)
      .Because("a waiter after the verdict is published returns it at once");
    await Assert.That(() => registry.PublishVerdict(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task WaitForVerdict_CompletesWhenTheVerdictIsPublishedAsync() {
    var registry = new CapabilityRegistry([_broker()], NullLogger<CapabilityRegistry>.Instance);
    var waiting = registry.WaitForVerdictAsync(CancellationToken.None);
    var verdict = CapabilityVerdict.Evaluate(registry.Statuses, []);

    var pendingBefore = !waiting.IsCompleted;
    registry.PublishVerdict(verdict);

    await Assert.That(pendingBefore).IsTrue();
    await Assert.That(await waiting).IsSameReferenceAs(verdict);
  }
}
