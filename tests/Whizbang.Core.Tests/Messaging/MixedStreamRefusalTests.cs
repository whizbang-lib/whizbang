// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Data.Postgres;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// <see cref="MixedStreamRefusal"/> turns the store's WHIZ133 refusal (SQLSTATE <c>WH133</c>) into
/// <see cref="MixedStreamEventException"/> and lets every other failure through unchanged. The store's wording is
/// built in SQL (migration 223), so these tests feed the translator that wording and the shapes it must survive.
/// </summary>
/// <docs>operations/diagnostics/whiz133</docs>
public class MixedStreamRefusalTests {
  private static readonly Guid _streamId = Guid.Parse("0199f0a1-0000-7000-8000-000000000001");
  private static readonly Guid _eventId = Guid.Parse("0199f0a1-0000-7000-8000-000000000002");

  private static string _storeMessage(Guid streamId, Guid eventId) =>
    $"WHIZ133: stream {streamId} holds ephemeral events (Probe.Contracts.UserIsTyping, Probe.Contracts), so the Sourced "
    + $"event Probe.Contracts.OrderPlaced, Probe.Contracts (event {eventId}) was refused. A stream never mixes ephemeral "
    + "and Sourced events: publish the ephemeral events on their own stream.";

  private static PostgresException _refusal(string message) =>
    new(message, "ERROR", "ERROR", MixedStreamRefusal.SQL_STATE);

  [Test]
  public async Task RunAsync_StoreRefusesAnEvent_ThrowsMixedStreamEventExceptionNamingTheStreamAndEventAsync() {
    var refusal = _refusal(_storeMessage(_streamId, _eventId));

    var thrown = await Assert.That(async () => await MixedStreamRefusal.RunAsync(() => throw refusal))
      .Throws<MixedStreamEventException>();

    await Assert.That(thrown!.StreamId).IsEqualTo(_streamId);
    await Assert.That(thrown.EventId).IsEqualTo(_eventId);
    await Assert.That(thrown.Message).IsEqualTo(refusal.MessageText)
      .Because("the caller reads the store's wording, the same text a dead-lettered refusal records");
    await Assert.That(thrown.InnerException).IsSameReferenceAs(refusal);
  }

  [Test]
  public async Task RunAsync_AnyOtherStoreFailure_PropagatesUnchangedAsync() {
    var other = new PostgresException("duplicate key value", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation);

    var thrown = await Assert.That(async () => await MixedStreamRefusal.RunAsync(() => throw other))
      .Throws<PostgresException>();

    await Assert.That(thrown).IsSameReferenceAs(other);
  }

  [Test]
  public async Task RunAsync_StoreSucceeds_CompletesAsync() {
    var ran = false;

    await MixedStreamRefusal.RunAsync(() => {
      ran = true;
      return Task.CompletedTask;
    });

    await Assert.That(ran).IsTrue();
  }

  [Test]
  public async Task ToException_MessageWithoutTheIds_KeepsTheMessageAndReportsEmptyIdsAsync() {
    var refusal = _refusal("WHIZ133: a store with different wording");

    var translated = MixedStreamRefusal.ToException(refusal);

    await Assert.That(translated.Message).IsEqualTo("WHIZ133: a store with different wording");
    await Assert.That(translated.StreamId).IsEqualTo(Guid.Empty);
    await Assert.That(translated.EventId).IsEqualTo(Guid.Empty);
  }

  [Test]
  public async Task ToException_MarkersFollowedByTooLittleText_ReportsEmptyIdsAsync() {
    var translated = MixedStreamRefusal.ToException(_refusal("WHIZ133: (event 123"));

    await Assert.That(translated.EventId).IsEqualTo(Guid.Empty);
    await Assert.That(translated.StreamId).IsEqualTo(Guid.Empty);
  }

  [Test]
  public async Task ToException_MarkersFollowedByTextThatIsNotAnId_ReportsEmptyIdsAsync() {
    var notAnId = new string('x', 36);

    var translated = MixedStreamRefusal.ToException(_refusal($"WHIZ133: stream {notAnId} (event {notAnId})"));

    await Assert.That(translated.StreamId).IsEqualTo(Guid.Empty);
    await Assert.That(translated.EventId).IsEqualTo(Guid.Empty);
  }
}
