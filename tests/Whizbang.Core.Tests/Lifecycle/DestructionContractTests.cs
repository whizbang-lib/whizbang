using System;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Lifecycle;

/// <summary>
/// Locks the E2 destruction hook contract (increment 1): the <see cref="DestructionContext"/> /
/// <see cref="DestructionResult"/> shape plus the <see cref="Disposition"/> / <see cref="DestructionReason"/>
/// / <see cref="DestructionGranularity"/> enums the <c>PreDestruction</c> / <c>PostDestruction</c> stages
/// carry. Inert until the reaper awaits the hook (increment 2).
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public class DestructionContractTests {
  [Test]
  public async Task Disposition_HasDeleteCompactArchiveCryptoShred_WithDeleteDefaultAsync() {
    await Assert.That(Enum.GetNames<Disposition>()).Contains("Delete");
    await Assert.That(Enum.GetNames<Disposition>()).Contains("Compact");
    await Assert.That(Enum.GetNames<Disposition>()).Contains("Archive");
    await Assert.That(Enum.GetNames<Disposition>()).Contains("CryptoShred");
    await Assert.That(Enum.GetValues<Disposition>()[0]).IsEqualTo(Disposition.Delete)
      .Because("Delete is the zero-value so an unset disposition defaults to the safe physical delete.");
  }

  [Test]
  public async Task DestructionReason_HasTheFourTriggersAsync() {
    await Assert.That(Enum.GetNames<DestructionReason>()).Contains("ConsumptionComplete");
    await Assert.That(Enum.GetNames<DestructionReason>()).Contains("TtlExpired");
    await Assert.That(Enum.GetNames<DestructionReason>()).Contains("StreamPurge");
    await Assert.That(Enum.GetNames<DestructionReason>()).Contains("Erasure");
  }

  [Test]
  public async Task DestructionGranularity_HasEventStreamPerspectiveRowAsync() {
    await Assert.That(Enum.GetNames<DestructionGranularity>()).Contains("Event");
    await Assert.That(Enum.GetNames<DestructionGranularity>()).Contains("Stream");
    await Assert.That(Enum.GetNames<DestructionGranularity>()).Contains("PerspectiveRow");
  }

  [Test]
  public async Task DestructionContext_DefaultsDeclaredToDelete_AndEmptyTargetsAsync() {
    var ctx = new DestructionContext {
      Reason = DestructionReason.ConsumptionComplete,
      Granularity = DestructionGranularity.Event,
    };
    await Assert.That(ctx.DeclaredDefault).IsEqualTo(Disposition.Delete)
      .Because("With no [Ephemeral(OnDestroy = …)], the declared default is a physical Delete.");
    await Assert.That(ctx.Targets.Count).IsEqualTo(0);
    await Assert.That(ctx.Scope).IsNull();
  }

  [Test]
  public async Task DestructionContext_CarriesTheWholeBatchOfTargetsAsync() {
    // The hook is batched: the context carries every event being reaped this cycle, possibly across streams.
    var a = new EphemeralDestructionTarget(Guid.NewGuid(), Guid.NewGuid(), "A");
    var b = new EphemeralDestructionTarget(Guid.NewGuid(), Guid.NewGuid(), "B");
    var ctx = new DestructionContext {
      Reason = DestructionReason.ConsumptionComplete,
      Granularity = DestructionGranularity.Event,
      Targets = [a, b],
    };
    await Assert.That(ctx.Targets.Count).IsEqualTo(2)
      .Because("A single hook invocation sees the whole batch, not one event at a time.");
    await Assert.That(ctx.Targets).Contains(a);
    await Assert.That(ctx.Targets).Contains(b);
  }

  [Test]
  public async Task DestructionResult_Proceed_CarriesDispositionAndNoCancelOrDeferAsync() {
    var result = DestructionResult.Proceed(Disposition.Archive);
    await Assert.That(result.Disposition).IsEqualTo(Disposition.Archive);
    await Assert.That(result.Cancel).IsFalse();
    await Assert.That(result.DeferUntil).IsNull();
  }

  [Test]
  public async Task DestructionResult_Proceed_DefaultsToDeleteAsync() {
    var result = DestructionResult.Proceed();
    await Assert.That(result.Disposition).IsEqualTo(Disposition.Delete);
  }

  [Test]
  public async Task DestructionResult_Canceled_SetsCancelAsync() {
    await Assert.That(DestructionResult.Canceled.Cancel).IsTrue()
      .Because("Cancel keeps the data ephemeral — no destruction, and no promotion to durable.");
    await Assert.That(DestructionResult.Canceled.DeferUntil).IsNull();
  }

  [Test]
  public async Task DestructionResult_Defer_SetsDeferUntilAsync() {
    var until = DateTimeOffset.UtcNow.AddHours(1);
    var result = DestructionResult.Defer(until);
    await Assert.That(result.DeferUntil).IsEqualTo(until);
    await Assert.That(result.Cancel).IsFalse();
  }
}
