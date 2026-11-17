// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

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
}
