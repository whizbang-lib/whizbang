// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Issue #1663: the receive gate no longer quarantines on age, so the loop it used to bound (a
/// session lock-loss storm, where the broker's delivery count stays at 1 on every redelivery) must
/// be bounded after storage. These tests drive the real store and the real poison detector: every
/// redelivery of an already-stored message increments its durable observation inside the store call,
/// before any handler can run, and layer 2 quarantines the message once its observations reach the
/// bound.
/// </summary>
/// <docs>fundamentals/dispatcher/routing#poison-messages</docs>
public class SessionStormBoundedAfterStorageTests : PostgresTestBase {

  private const int PARTITION_COUNT = 100;
  private const int BOUND = 5;
  private static readonly int[] _redeliveryCounts = [2, 3, 4, 5, 6];

  [Test]
  public async Task RedeliveriesOfAStoredMessage_IncrementTheObservationBeforeAnyHandlerRunsAsync() {
    var coordinator = _coordinator();
    var message = _inbox((Guid)TrackedGuid.New());

    var first = await coordinator.StoreInboxMessagesWithObservationsAsync([message], PARTITION_COUNT);
    var reported = new List<int>();
    for (var delivery = 2; delivery <= 6; delivery++) {
      var observations = await coordinator.StoreInboxMessagesWithObservationsAsync([message], PARTITION_COUNT);
      reported.Add(observations.Single(o => o.MessageId == message.MessageId).ObservationCount);
    }

    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    var (rows, attempts) = await connection.QuerySingleAsync<(long Rows, int Attempts)>(
      "SELECT count(*), max(s.attempts) FROM wh_inbox i JOIN wh_inbox_state s ON s.message_id = i.message_id "
      + "WHERE i.message_id = @id", new { id = message.MessageId });

    await Assert.That(first).IsEmpty().Because("a first delivery is not a redelivery");
    await Assert.That(reported).IsEquivalentTo(_redeliveryCounts)
      .Because("each redelivery is counted by the store call itself, so the bound needs no handler to run");
    await Assert.That(rows).IsEqualTo(1L).Because("the store is idempotent: redeliveries never duplicate the row");
    await Assert.That(attempts).IsEqualTo(0).Because("nothing has claimed or handled the message yet");
  }

  [Test]
  public async Task SessionStorm_StoredMessageRedeliveredAtCountOne_IsQuarantinedByLayer2AtTheBoundAsync() {
    // The storm: the same message id arrives again and again, each time as a broker first delivery
    // (count 1, so nothing at the broker or the receive gate can bound it). It was stored on the first
    // arrival and claimed for processing once (the claim starts attempt 1, before the handler).
    var coordinator = _coordinator();
    var detector = new PoisonMessageDetector(
      Options.Create(new PoisonMessageOptions { MaxDurableObservations = BOUND }),
      NullLogger<PoisonMessageDetector>.Instance,
      new System.Diagnostics.Metrics.Meter("Whizbang.Data.Dapper.Postgres.Tests.SessionStorm"));
    var message = _inbox((Guid)TrackedGuid.New());

    await coordinator.StoreInboxMessagesWithObservationsAsync([message], PARTITION_COUNT);
    await _claimAsync(message.MessageId);

    var quarantinedAtDelivery = 0;
    for (var delivery = 2; delivery <= BOUND + 3 && quarantinedAtDelivery == 0; delivery++) {
      var observation = (await coordinator.StoreInboxMessagesWithObservationsAsync([message], PARTITION_COUNT))
        .Single(o => o.MessageId == message.MessageId);
      var verdict = detector.Evaluate(_layer2Context(observation));
      if (verdict.ShouldQuarantine) {
        quarantinedAtDelivery = delivery;
        await Assert.That(verdict.Reason).IsEqualTo(PoisonQuarantineReason.ObservationCountExceeded);
      }
    }

    await Assert.That(quarantinedAtDelivery).IsEqualTo(BOUND)
      .Because("a stored message that keeps coming back is quarantined once its durable observations "
             + "reach the bound, with the broker delivery count never having moved");
  }

  [Test]
  public async Task StoredButNeverClaimed_RedeliveriesPastTheBound_AreNotQuarantinedAsync() {
    // The evidence rule layer 2 already applies: observations count DELIVERIES, so a message fanned out
    // to more subscriptions than the bound crosses it without any attempt. Until a claim has started
    // processing, crossing the bound is not a loop. Locked here so the storm bound above is read with
    // its precondition.
    var coordinator = _coordinator();
    var detector = new PoisonMessageDetector(
      Options.Create(new PoisonMessageOptions { MaxDurableObservations = BOUND }),
      NullLogger<PoisonMessageDetector>.Instance,
      new System.Diagnostics.Metrics.Meter("Whizbang.Data.Dapper.Postgres.Tests.SessionStorm"));
    var message = _inbox((Guid)TrackedGuid.New());

    await coordinator.StoreInboxMessagesWithObservationsAsync([message], PARTITION_COUNT);
    var verdicts = new List<PoisonVerdict>();
    for (var delivery = 2; delivery <= BOUND + 3; delivery++) {
      var observation = (await coordinator.StoreInboxMessagesWithObservationsAsync([message], PARTITION_COUNT))
        .Single(o => o.MessageId == message.MessageId);
      verdicts.Add(detector.Evaluate(_layer2Context(observation)));
    }

    await Assert.That(verdicts.Any(v => v.ShouldQuarantine)).IsFalse();
  }

  // The context the consumer worker builds at the inbox gate: durable facts only.
  private static PoisonEvaluationContext _layer2Context(InboxRedeliveryObservation observation) =>
    new(
      MessageId: observation.MessageId.ToString(),
      FirstEnqueuedAt: null,
      BrokerDeliveryCount: null,
      DurableObservationCount: observation.ObservationCount,
      Now: new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero)) { ProcessingAttempts = observation.ProcessingAttempts };

  /// <summary>Claims the message through the real claim function, which counts attempt 1.</summary>
  private async Task _claimAsync(Guid messageId) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    var instanceId = (Guid)TrackedGuid.New();
    var now = await connection.ExecuteScalarAsync<DateTimeOffset>("SELECT now()");
    await connection.ExecuteAsync(@"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at)
      VALUES (@instanceId, 'TestService', 'test-host', 1234, @now, @now)", new { instanceId, now });
    var claimed = await connection.QueryAsync<Guid>(@"
      SELECT message_id FROM claim_orphaned_inbox(@instanceId, 0, 1, @lease, @claimAt, @partitions, @staleCutoff)",
      new {
        instanceId,
        lease = now.AddMinutes(5),
        claimAt = now.AddSeconds(1),
        partitions = PARTITION_COUNT,
        staleCutoff = now.AddSeconds(-30),
      });
    await Assert.That(claimed).Contains(messageId).Because("the test needs the real claim to start attempt 1");
  }

  private DapperWorkCoordinator _coordinator() =>
    new(
      ConnectionString,
      new JsonSerializerOptions { TypeInfoResolver = InfrastructureJsonContext.Default },
      NullLogger<DapperWorkCoordinator>.Instance);

  private static InboxMessage _inbox(Guid messageId) => new() {
    MessageId = messageId,
    HandlerName = "TestHandler",
    Envelope = new MessageEnvelope<JsonElement>(MessageId.From(messageId), JsonElement.Parse("{\"p\":1}"), []),
    EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
    MessageType = "Test.X, Test",
    StreamId = (Guid)TrackedGuid.New(),
    IsEvent = true,
    Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] },
  };
}
