// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

#pragma warning disable CA1707 // Test method naming uses underscores by convention

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// The transport converges an event subscription on the excluded subjects its destination carries (#2175), and leaves a
/// destination without them (an inbox, a custom destination) exactly as before.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AzureServiceBusTransport.cs</code-under-test>
[Timeout(10_000)]
public class AzureServiceBusExcludedEventTypesSubscribeTests {
  private const string TOPIC = "myapp.orders.events";

  [Test]
  public async Task Subscribe_DestinationWithoutTheKey_NeverReadsRulesAsync(CancellationToken cancellationToken) {
    var admin = new StatefulRuleAdminClient(_default());

    await _subscribeAsync(admin, metadata: null, cancellationToken: cancellationToken);

    await Assert.That(admin.GetRulesCalls).IsEqualTo(0);
  }

  [Test]
  public async Task Subscribe_ExcludedSubjects_AppliesTheRuleAsync(CancellationToken cancellationToken) {
    var admin = new StatefulRuleAdminClient(_default());

    await _subscribeAsync(admin, """["myapp.orders.events.orderpinged", 7, " "]""", cancellationToken: cancellationToken);

    var rule = admin.Rules.Single();
    await Assert.That(rule.Name).IsEqualTo(ServiceBusEntityProvisioning.EXCLUDED_EVENT_TYPES_RULE_NAME);
    await Assert.That(((SqlRuleFilter)rule.Filter).SqlExpression)
      .IsEqualTo("sys.Label IS NULL OR NOT (sys.Label IN ('myapp.orders.events.orderpinged'))")
      .Because("non-string and blank entries are skipped");
  }

  [Test]
  public async Task Subscribe_EmptyList_LeavesDefaultAndLogsTheEffectiveRuleAsync(CancellationToken cancellationToken) {
    var admin = new StatefulRuleAdminClient(_default());
    var logger = new RecordingTransportLogger();

    await _subscribeAsync(admin, "[]", logger: logger, cancellationToken: cancellationToken);

    await Assert.That(admin.GetRulesCalls).IsEqualTo(1);
    await Assert.That(admin.Writes).IsEmpty();
    await Assert.That(logger.Contains(LogLevel.Information, "every event")).IsTrue();
  }

  [Test]
  public async Task Subscribe_KeyIsNotAnArray_IsIgnoredAsync(CancellationToken cancellationToken) {
    var admin = new StatefulRuleAdminClient(_default());

    await _subscribeAsync(admin, "\"myapp.orders.events.orderpinged\"", cancellationToken: cancellationToken);

    await Assert.That(admin.GetRulesCalls).IsEqualTo(0);
  }

  [Test]
  public async Task Subscribe_AutoProvisionDisabled_WithSubjects_WarnsAndTouchesNothingAsync(CancellationToken cancellationToken) {
    var admin = new StatefulRuleAdminClient(_default());
    var logger = new RecordingTransportLogger();

    await _subscribeAsync(admin, """["myapp.orders.events.orderpinged"]""", autoProvision: false, logger: logger,
      cancellationToken: cancellationToken);

    await Assert.That(admin.GetRulesCalls).IsEqualTo(0);
    await Assert.That(logger.Contains(LogLevel.Warning, "excluded event types")).IsTrue();
  }

  [Test]
  public async Task Subscribe_AutoProvisionDisabled_EmptyList_IsSilentAsync(CancellationToken cancellationToken) {
    var admin = new StatefulRuleAdminClient(_default());
    var logger = new RecordingTransportLogger();

    await _subscribeAsync(admin, "[]", autoProvision: false, logger: logger, cancellationToken: cancellationToken);

    await Assert.That(admin.GetRulesCalls).IsEqualTo(0);
    await Assert.That(logger.Contains(LogLevel.Warning, "excluded event types")).IsFalse();
  }

  [Test]
  public async Task Subscribe_NoAdminClient_WithSubjects_WarnsAsync(CancellationToken cancellationToken) {
    var logger = new RecordingTransportLogger();

    await _subscribeAsync(null, """["myapp.orders.events.orderpinged"]""", logger: logger,
      cancellationToken: cancellationToken);

    await Assert.That(logger.Contains(LogLevel.Warning, "excluded event types")).IsTrue();
  }

  private static async Task _subscribeAsync(
      StatefulRuleAdminClient? admin,
      string? metadata,
      bool autoProvision = true,
      RecordingTransportLogger? logger = null,
      CancellationToken cancellationToken = default) {
    var values = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = _json("\"OrderService\"")
    };
    if (metadata is not null) {
      values[TransportSubscriptionBuilder.ExcludedEventSubjectsMetadataKey] = _json(metadata);
    }
    await using var transport = new AzureServiceBusTransport(
      new RaisableServiceBusClient(),
      AsbTransportTestData.CombinedOptions,
      new AzureServiceBusOptions { EnableSessions = false, AutoProvisionInfrastructure = autoProvision },
      logger ?? new RecordingTransportLogger(),
      admin);

    using var subscription = await transport.SubscribeAsync(
      (_, _, _) => Task.CompletedTask, new TransportDestination(TOPIC, "#", values), cancellationToken);
  }

  private static RuleProperties _default() => ServiceBusModelFactory.RuleProperties("$Default", new TrueRuleFilter());

  private static JsonElement _json(string raw) {
    using var doc = JsonDocument.Parse(raw);
    return doc.RootElement.Clone();
  }
}
