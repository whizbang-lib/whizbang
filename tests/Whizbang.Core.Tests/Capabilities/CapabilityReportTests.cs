// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Capabilities;

/// <summary>
/// The startup verdict, in one format for every provider: per provider and scope, one Information block
/// listing exactly the usable capabilities with their purpose, and the unusable ones at Debug with the fix.
/// </summary>
/// <docs>operations/infrastructure/capabilities#verdict</docs>
public class CapabilityReportTests {
  private static readonly CapabilityDescriptor _vector = FakeCapabilityProvider.Describe("postgres", "vector", "vector fields");
  private static readonly CapabilityDescriptor _trgm = FakeCapabilityProvider.Describe("postgres", "pg_trgm", "substring indexes");
  private static readonly CapabilityDescriptor _squeeze = FakeCapabilityProvider.Describe("postgres", "pg_squeeze", "online rewrite") with {
    Remedies = new Dictionary<CapabilityState, string> { [CapabilityState.NotPermitted] = "allowlist it" },
  };
  private static readonly CapabilityDescriptor _delayed = FakeCapabilityProvider.Describe("broker", "delayed_exchange", "delayed delivery");

  private static IReadOnlyList<CapabilityStatus> _mixed() => [
    new(_vector, "orders-db", CapabilityState.Ready, "0.8.0", "Installed", null),
    new(_trgm, "orders-db", CapabilityState.Provisionable, null, null, null),
    new(_squeeze, "orders-db", CapabilityState.NotPermitted, null, "NotAllowlisted", null),
    new(_delayed, "vhost-a", CapabilityState.Missing, null, null, null),
  ];

  [Test]
  public async Task FormatUsable_ListsExactlyTheUsableCapabilitiesWithTheirPurposeAsync() {
    var lines = CapabilityReport.FormatUsable(_mixed()).Split('\n');

    await Assert.That(lines.Length).IsEqualTo(2);
    await Assert.That(lines[0]).IsEqualTo("  postgres/vector (Ready, 0.8.0; Installed): vector fields");
    await Assert.That(lines[1]).IsEqualTo("  postgres/pg_trgm (Provisionable): substring indexes");
  }

  [Test]
  public async Task FormatUsable_NothingUsable_SaysNoneAsync() {
    await Assert.That(CapabilityReport.FormatUsable([new(_squeeze, "orders-db", CapabilityState.Missing, null, null, null)]))
      .IsEqualTo("  (none)");
  }

  [Test]
  public async Task FormatUnusable_ListsEachWithStateDetailPurposeAndFixAsync() {
    var lines = CapabilityReport.FormatUnusable(_mixed()).Split('\n');

    await Assert.That(lines.Length).IsEqualTo(2);
    await Assert.That(lines[0]).IsEqualTo("  postgres/pg_squeeze (NotPermitted; NotAllowlisted): online rewrite. Fix: allowlist it");
    await Assert.That(lines[1]).IsEqualTo($"  broker/delayed_exchange (Missing): delayed delivery. Fix: {_delayed.Remedy(CapabilityState.Missing)}");
  }

  [Test]
  public async Task FormatUnusable_AddsTheReasonTheProbeGaveAsync() {
    var text = CapabilityReport.FormatUnusable([new(_vector, "orders-db", CapabilityState.Unknown, null, null, "no route to host")]);

    await Assert.That(text).StartsWith("  postgres/vector (Unknown: no route to host): vector fields. Fix: ");
  }

  [Test]
  public async Task Log_WritesOneBlockPairPerProviderAndScopeAsync() {
    var logger = new FakeLogger();

    CapabilityReport.Log(logger, _mixed());

    var records = logger.Collector.GetSnapshot();
    var information = records.Where(r => r.Level == LogLevel.Information).ToArray();
    await Assert.That(information.Length).IsEqualTo(2);
    await Assert.That(information[0].Message).StartsWith("Capabilities of postgres on 'orders-db': 2 usable of 3");
    await Assert.That(information[0].Message).DoesNotContain("pg_squeeze");
    await Assert.That(information[1].Message).StartsWith("Capabilities of broker on 'vhost-a': 0 usable of 1");
    var debug = records.Where(r => r.Level == LogLevel.Debug).ToArray();
    await Assert.That(debug.Length).IsEqualTo(2);
    await Assert.That(debug[0].Message).Contains("postgres/pg_squeeze (NotPermitted; NotAllowlisted)");
    await Assert.That(debug[1].Message).Contains("broker/delayed_exchange (Missing)");
  }

  [Test]
  public async Task Log_EverythingUsable_WritesNoDebugBlockAsync() {
    var logger = new FakeLogger();

    CapabilityReport.Log(logger, [new(_vector, "orders-db", CapabilityState.Ready, null, null, null)]);

    await Assert.That(logger.Collector.GetSnapshot().Single().Level).IsEqualTo(LogLevel.Information);
  }

  [Test]
  public async Task Log_InformationOnAndDebugOff_WritesOnlyTheUsableBlockAsync() {
    var logger = new FakeLogger();
    logger.ControlLevel(LogLevel.Debug, enabled: false);

    CapabilityReport.Log(logger, _mixed());

    await Assert.That(logger.Collector.GetSnapshot().All(r => r.Level == LogLevel.Information)).IsTrue();
  }

  [Test]
  public async Task Log_BothLevelsOff_WritesNothingAsync() {
    var logger = new FakeLogger();
    logger.ControlLevel(LogLevel.Information, enabled: false);
    logger.ControlLevel(LogLevel.Debug, enabled: false);

    CapabilityReport.Log(logger, _mixed());

    await Assert.That(logger.Collector.Count).IsEqualTo(0);
  }
}
