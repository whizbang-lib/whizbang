#pragma warning disable CA1707

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Locks the no-rebroadcast guard predicate (<see cref="NoRebroadcastGuard"/>): the outbox-enqueue
/// boundary suppresses a publish only when its source envelope carries
/// <see cref="EventFlags.NoRebroadcast"/> (a fan-out child). A null source (fresh local publish) and an
/// unflagged source pass through.
/// </summary>
[Category("Messaging")]
public class NoRebroadcastGuardTests {

  [Test]
  public async Task ShouldSuppress_NullSource_IsFalseAsync() {
    await Assert.That(NoRebroadcastGuard.ShouldSuppress(null)).IsFalse()
      .Because("A fresh local publish has no source — it is not a re-broadcast.");
  }

  [Test]
  public async Task ShouldSuppress_UnflaggedSource_IsFalseAsync() {
    await Assert.That(NoRebroadcastGuard.ShouldSuppress(_envelope(EventFlags.None))).IsFalse();
  }

  [Test]
  public async Task ShouldSuppress_NoRebroadcastSource_IsTrueAsync() {
    await Assert.That(NoRebroadcastGuard.ShouldSuppress(_envelope(EventFlags.NoRebroadcast))).IsTrue()
      .Because("A fan-out child carries NoRebroadcast — any publish it triggers must be dropped.");
  }

  [Test]
  public async Task ShouldSuppress_NoRebroadcastAmongOtherFlags_IsTrueAsync() {
    await Assert.That(NoRebroadcastGuard.ShouldSuppress(_envelope(EventFlags.NoRebroadcast | EventFlags.Composite))).IsTrue()
      .Because("The guard checks the NoRebroadcast bit, not exact flag equality.");
  }

  private static MessageEnvelope<JsonElement> _envelope(EventFlags flags) => new() {
    MessageId = MessageId.New(),
    Payload = JsonDocument.Parse("{}").RootElement,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    Flags = flags,
  };
}
