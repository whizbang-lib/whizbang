// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Rules;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2362: an origin refuses a repair the integrity comparison asked for while the pair's rules differ, and one from a
/// release that sent none. An operator's request and a subscription backfill window are not judged by a digest, so they
/// are served whatever the rules.
/// </summary>
public partial class RedeliveryRequestReceptorTests {
  private RedeliveryRequestReceptor _pairGuardReceptor(out SelectingCoordinator coordinator, out CaptureTransport transport,
      out ServiceProvider provider) {
    coordinator = new SelectingCoordinator { Selection = [_evt(TrackedGuid.New().Value, TrackedGuid.New().Value, 1)] };
    transport = new CaptureTransport();
    provider = _buildProvider(coordinator, transport);
    return new RedeliveryRequestReceptor(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<RedeliveryRequestReceptor>.Instance);
  }

  [Test]
  public async Task AComparisonsRepair_WithoutTheRequestersRules_IsRefusedAsync() {
    var receptor = _pairGuardReceptor(out var coordinator, out var transport, out var provider);
    await using var _ = provider;

    await receptor.HandleAsync(new RequestRedeliveryCommand { RuleFingerprint = null, RequesterService = "pre-fingerprint-svc", Topic = "inbox" });

    await Assert.That(coordinator.Requests).IsEmpty().Because("nothing is selected for a halted pair");
    await Assert.That(transport.Published).IsEmpty();
  }

  [Test]
  public async Task AnOperatorsRequest_OrABackfillWindow_IsServedWithoutTheRequestersRulesAsync() {
    var receptor = _pairGuardReceptor(out var coordinator, out var transport, out var provider);
    await using var _ = provider;

    await receptor.HandleAsync(new RequestRedeliveryCommand {
      RuleFingerprint = null,
      RequesterService = "pre-fingerprint-svc",
      Topic = "inbox",
      OperatorRequested = true,
    });
    await receptor.HandleAsync(new RequestRedeliveryCommand {
      RuleFingerprint = null,
      RequesterService = "pre-fingerprint-svc",
      Topic = "inbox",
      BackfillId = TrackedGuid.New().Value,
      BackfillWindow = 1,
    });

    await Assert.That(coordinator.Requests.Count).IsGreaterThanOrEqualTo(2)
      .Because("neither is judged by a digest, so different rules cannot make it wrong");
    await Assert.That(transport.Published).IsNotEmpty();
  }
  private static readonly RuleFingerprint _otherRules = RuleFingerprint.From(new RuleSet(
    RuleSemantics.VERSION + 1, RuleSemantics.ELIGIBILITY_SQL_VERSION, new Dictionary<string, string>()));

  [Test]
  public async Task AComparisonsRepair_FromAPeerWithOtherRules_IsRefusedAndCountedAsIgnoredAsync() {
    var metrics = new Whizbang.Core.Observability.StreamIntegrityMetrics(new Whizbang.Core.Observability.WhizbangMetrics(
      meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()));
    var ignored = new List<(long Value, string? Reason)>();
    using var listener = new System.Diagnostics.Metrics.MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument.Meter, metrics.GapsDetected.Meter) && instrument.Name == "whizbang.stream_integrity.redelivery_requests_ignored") {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      string? reason = null;
      foreach (var tag in tags) {
        if (tag.Key == "reason") {
          reason = tag.Value as string;
        }
      }
      lock (ignored) {
        ignored.Add((value, reason));
      }
    });
    listener.Start();
    var coordinator = new SelectingCoordinator { Selection = [_evt(TrackedGuid.New().Value, TrackedGuid.New().Value, 1)] };
    var transport = new CaptureTransport();
    await using var sp = _buildOwningProvider(coordinator, transport, new CaptureSerializer(), "orders-svc", metrics);
    var receptor = new RedeliveryRequestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RedeliveryRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestRedeliveryCommand { RuleFingerprint = _otherRules, RequesterService = "damaged-svc", Topic = "inbox" });
    listener.RecordObservableInstruments();

    await Assert.That(coordinator.Requests).IsEmpty().Because("nothing is selected for a halted pair");
    await Assert.That(transport.Published).IsEmpty();
    await Assert.That(ignored).IsEquivalentTo([(1L, (string?)"pair_halted")])
      .Because("the counter is passive, so it reports its running total when observed: one refusal, for a halted pair");

    await receptor.HandleAsync(new RequestRedeliveryCommand { RuleFingerprint = IntegrityPairGuard.Fallback.Stamp(null), RequesterService = "damaged-svc", Topic = "inbox" });
    await Assert.That(coordinator.Requests).IsNotEmpty().Because("positive control: the same request with matching rules is served");
  }

  [Test]
  public async Task AComparisonsRepair_FromAPeerWithOtherRules_IsRefused_OnAHostWithoutMetricsAsync() {
    var coordinator = new SelectingCoordinator { Selection = [_evt(TrackedGuid.New().Value, TrackedGuid.New().Value, 1)] };
    var transport = new CaptureTransport();
    await using var sp = _buildOwningProvider(coordinator, transport, new CaptureSerializer(), "orders-svc");
    var receptor = new RedeliveryRequestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RedeliveryRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestRedeliveryCommand { RuleFingerprint = _otherRules, RequesterService = "damaged-svc", Topic = "inbox" });

    await Assert.That(coordinator.Requests).IsEmpty().Because("the refusal does not depend on a meter being configured");
    await Assert.That(transport.Published).IsEmpty();
  }
}
