// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The decisions the checkpoint and manifest receptors share before asking an origin to redeliver:
/// which ledger bounds the asking, where the redelivery is sent back to, and which window it covers.
/// Each outcome is asserted here once; the receptors carry no decision of their own for them.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IntegrityManifestReceptors.cs</code-under-test>
[Category("Shard1")]
public class IntegrityRepairSupportTests {

  // ── ResolveLedger ──────────────────────────────────────────────────────

  [Test]
  public async Task ResolveLedger_WhenTheInterfaceIsRegistered_PrefersItOverTheInProcessLedgerAsync() {
    // The interface registration is the coordinator-backed, fleet-wide ledger. Falling through to
    // the in-process one would bound asking per pod instead of per fleet.
    var fleetWide = new IntegrityRepairLedger();
    var inProcess = new IntegrityRepairLedger();
    var services = new ServiceCollection()
      .AddSingleton<IIntegrityRepairLedger>(fleetWide)
      .AddSingleton(inProcess)
      .BuildServiceProvider();

    var ledger = IntegrityRepairSupport.ResolveLedger(services);

    await Assert.That(ReferenceEquals(ledger, fleetWide)).IsTrue();
  }

  [Test]
  public async Task ResolveLedger_WhenOnlyTheInProcessLedgerIsRegistered_UsesItAsync() {
    var inProcess = new IntegrityRepairLedger();
    var services = new ServiceCollection().AddSingleton(inProcess).BuildServiceProvider();

    var ledger = IntegrityRepairSupport.ResolveLedger(services);

    await Assert.That(ReferenceEquals(ledger, inProcess)).IsTrue()
      .Because("the registered ledger carries the backoff state across checkpoints; a fresh one would forget it");
  }

  [Test]
  public async Task ResolveLedger_WhenNoLedgerIsRegistered_FallsBackToAFreshInProcessLedgerAsync() {
    var services = new ServiceCollection().BuildServiceProvider();

    var ledger = IntegrityRepairSupport.ResolveLedger(services);

    await Assert.That(ledger).IsTypeOf<IntegrityRepairLedger>()
      .Because("an unwired host still gets bounded asking rather than none, and must not throw");
  }

  // ── ResolveRepairTopic ─────────────────────────────────────────────────

  [Test]
  public async Task ResolveRepairTopic_WhenConfigured_UsesTheConfiguredTopicOverTheConsumerDestinationAsync() {
    var services = _servicesWithDestinations("inbox");

    var topic = IntegrityRepairSupport.ResolveRepairTopic(
      new StreamIntegrityOptions { RepairTopic = "repairs" }, services);

    await Assert.That(topic).IsEqualTo("repairs");
  }

  [Test]
  public async Task ResolveRepairTopic_WhenUnconfigured_UsesTheFirstConsumerDestinationAsync() {
    var services = _servicesWithDestinations("inbox", "second");

    var topic = IntegrityRepairSupport.ResolveRepairTopic(new StreamIntegrityOptions(), services);

    await Assert.That(topic).IsEqualTo("inbox");
  }

  [Test]
  public async Task ResolveRepairTopic_WhenTheConsumerHasNoDestinations_HasNoTopicAsync() {
    var services = _servicesWithDestinations();

    var topic = IntegrityRepairSupport.ResolveRepairTopic(new StreamIntegrityOptions(), services);

    await Assert.That(topic).IsNull()
      .Because("with nowhere to receive the redelivery, the caller must skip the request rather than send one with no return address");
  }

  [Test]
  public async Task ResolveRepairTopic_WhenNoConsumerIsRegistered_HasNoTopicAsync() {
    var services = new ServiceCollection().BuildServiceProvider();

    var topic = IntegrityRepairSupport.ResolveRepairTopic(new StreamIntegrityOptions(), services);

    await Assert.That(topic).IsNull();
  }

  // ── RedeliveryWindow ───────────────────────────────────────────────────

  [Test]
  public async Task RedeliveryWindow_ForAWindowedManifest_MapsTheHalfOpenWindowOntoTheCommandsBoundsAsync() {
    // [100, 300) becomes exclusive floor 99 and inclusive ceiling 299: exactly the compared slice.
    var window = IntegrityRepairSupport.RedeliveryWindow(_manifest(since: 100, through: 300));

    await Assert.That(window.From).IsEqualTo(99L);
    await Assert.That(window.To).IsEqualTo(299L);
  }

  [Test]
  public async Task RedeliveryWindow_ForAWindowStartingAtTheBeginning_LeavesTheFloorOpenAsync() {
    // A since of 0 has no predecessor to exclude; -1 would be a floor no sequence can sit under.
    var window = IntegrityRepairSupport.RedeliveryWindow(_manifest(since: 0, through: 300));

    await Assert.That(window.From).IsNull();
    await Assert.That(window.To).IsEqualTo(299L);
  }

  [Test]
  public async Task RedeliveryWindow_ForALegacyUnwindowedManifest_LeavesBothBoundsOpenAsync() {
    var window = IntegrityRepairSupport.RedeliveryWindow(_manifest(since: null, through: null));

    await Assert.That(window.From).IsNull()
      .Because("an unwindowed manifest compared whole history, so the redelivery covers whole history");
    await Assert.That(window.To).IsNull();
  }

  private static ServiceProvider _servicesWithDestinations(params string[] addresses) {
    var consumer = new TransportConsumerOptions();
    foreach (var address in addresses) {
      consumer.Destinations.Add(new TransportDestination(address));
    }
    return new ServiceCollection().AddSingleton(consumer).BuildServiceProvider();
  }

  private static IntegrityManifest _manifest(long? since, long? through) => new() {
    ManifestStreamId = Guid.NewGuid(),
    OriginServiceId = Guid.NewGuid(),
    OriginServiceName = "origin-svc",
    SinceSequence = since,
    ComputedThrough = through,
  };
}
