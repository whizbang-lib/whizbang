using System.Text.Json;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Two whole entry points on <see cref="EFCoreWorkCoordinator{TContext}"/> that every other suite
/// reaches only through a fake:
/// <list type="bullet">
///   <item><c>StoreInboxMessagesWithObservationsAsync</c> — the phase 8.5 store that also reports
///   how many times this service has durably recorded each message id. Poison detection's layer 2
///   acts on that number, so a store that reports nothing quietly disables the bound, and one that
///   reports a first sighting as a redelivery quarantines healthy traffic.</item>
///   <item><c>GatherStatisticsAsync</c> — the operator-facing backlog snapshot. Its four counts
///   come from one hand-written SQL statement against four tables; a schema or column rename
///   breaks it at runtime and nowhere else.</item>
/// </list>
/// Both run against real PostgreSQL because both ARE SQL — a fake proves nothing about either.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class EFCoreWorkCoordinatorObservationAndStatsTests : EFCoreTestBase {

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _build(WorkCoordinationDbContext ctx)
    => new(ctx, JsonContextRegistry.CreateCombinedOptions());

  private static InboxMessage _makeInbox(Guid messageId, Guid streamId) {
    var envelope = new MessageEnvelope<JsonElement>(
      MessageId.From(messageId),
      JsonDocument.Parse("{\"p\":1}").RootElement,
      []);
    return new InboxMessage {
      MessageId = messageId,
      HandlerName = "ObservationHandler",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
      MessageType = "Test.X, Test",
      StreamId = streamId,
      IsEvent = true,
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] },
    };
  }

  // ── StoreInboxMessagesWithObservationsAsync ─────────────────────────────

  [Test]
  public async Task StoreWithObservations_FirstDelivery_ReportsNothingAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _build(ctx);
    var message = _makeInbox(Guid.CreateVersion7(), Guid.CreateVersion7());

    var observations = await coordinator.StoreInboxMessagesWithObservationsAsync([message], partitionCount: 2);

    await Assert.That(observations).IsEmpty()
      .Because("a first sighting is not a redelivery — reporting it would make every message "
             + "look like it had been seen before, and poison detection acts on that number");
  }

  [Test]
  public async Task StoreWithObservations_SecondDeliveryOfTheSameId_ReportsTheDurableCountAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _build(ctx);
    var messageId = Guid.CreateVersion7();
    var message = _makeInbox(messageId, Guid.CreateVersion7());

    await coordinator.StoreInboxMessagesWithObservationsAsync([message], partitionCount: 2);
    var observations = await coordinator.StoreInboxMessagesWithObservationsAsync([message], partitionCount: 2);

    await Assert.That(observations.Count).IsEqualTo(1)
      .Because("the broker handed this service an id it had already recorded — that is exactly "
             + "what layer 2 poison detection needs to hear about");
    var observed = observations[0];
    await Assert.That(observed.MessageId).IsEqualTo(messageId);
    await Assert.That(observed.ObservationCount).IsEqualTo(2)
      .Because("the count includes the current delivery, so the second store reports two");
    await Assert.That(observed.ProcessingAttempts).IsNotNull()
      .Because("the inbox row still exists, so attempts are MEASURED — null would mean unmeasured "
             + "and must never be read as zero failures");
  }

  [Test]
  public async Task StoreWithObservations_MixedBatch_ReportsOnlyTheRedeliveredIdAsync() {
    // The realistic shape: a redelivery arrives inside a batch of new messages. Reporting the
    // whole batch would quarantine healthy traffic; reporting none would disable the bound.
    await using var ctx = CreateDbContext();
    var coordinator = _build(ctx);
    var repeatedId = Guid.CreateVersion7();
    var repeated = _makeInbox(repeatedId, Guid.CreateVersion7());
    await coordinator.StoreInboxMessagesWithObservationsAsync([repeated], partitionCount: 2);

    var fresh = _makeInbox(Guid.CreateVersion7(), Guid.CreateVersion7());
    var observations = await coordinator.StoreInboxMessagesWithObservationsAsync(
      [fresh, repeated], partitionCount: 2);

    await Assert.That(observations.Count).IsEqualTo(1);
    await Assert.That(observations[0].MessageId).IsEqualTo(repeatedId)
      .Because("only the id this service had already recorded is a redelivery");
  }

  [Test]
  public async Task StoreWithObservations_EmptyBatch_ReturnsEmptyWithoutQueryingAsync() {
    await using var ctx = CreateDbContext();

    var observations = await _build(ctx).StoreInboxMessagesWithObservationsAsync([], partitionCount: 2);

    await Assert.That(observations).IsEmpty();
  }

  // ── GatherStatisticsAsync ───────────────────────────────────────────────

  [Test]
  public async Task GatherStatistics_OnAnEmptySchema_ReturnsZeroesRatherThanFailingAsync() {
    // The counts come from one hand-written statement across four tables. If any of them is
    // renamed or moves schema, this is where it surfaces — an operator asking for backlog gets
    // an exception instead of a number.
    await using var ctx = CreateDbContext();

    var stats = await _build(ctx).GatherStatisticsAsync();

    await Assert.That(stats).IsNotNull();
    await Assert.That(stats.PendingInbox).IsEqualTo(0);
    await Assert.That(stats.PendingOutbox).IsEqualTo(0);
    await Assert.That(stats.PendingPerspectiveEvents).IsEqualTo(0);
  }

  [Test]
  public async Task GatherStatistics_CountsOnlyUnprocessedInboxRowsAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _build(ctx);
    var pendingId = Guid.CreateVersion7();
    var processedId = Guid.CreateVersion7();
    await coordinator.StoreInboxMessagesAsync(
      [_makeInbox(pendingId, Guid.CreateVersion7()), _makeInbox(processedId, Guid.CreateVersion7())],
      partitionCount: 2);

    var before = await coordinator.GatherStatisticsAsync();
    await _markInboxProcessedAsync(processedId);
    var after = await coordinator.GatherStatisticsAsync();

    await Assert.That(before.PendingInbox).IsEqualTo(2);
    await Assert.That(after.PendingInbox).IsEqualTo(1)
      .Because("backlog means work still to do — counting processed rows would report a backlog "
             + "that never drains no matter how much the service gets through");
  }

  private async Task _markInboxProcessedAsync(Guid messageId) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE wh_inbox SET processed_at = now() WHERE message_id = @id";
    cmd.Parameters.AddWithValue("id", messageId);
    await cmd.ExecuteNonQueryAsync();
  }
}
