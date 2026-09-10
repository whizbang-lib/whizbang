using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.Tests;

/// <summary>
/// The contract every <see cref="ISubscription"/> keeps: active on creation, inactive after a pause, active again
/// after a resume, inactive for good after a dispose, and idempotent under a repeated pause, resume or dispose.
/// Abstract so that it runs against real subscriptions only: a test that exercised a fake declared in the same
/// file verified nothing but the fake. Each transport that ships a subscription inherits this class and creates
/// its own.
/// </summary>
/// <tests>src/Whizbang.Core/Transports/ISubscription.cs</tests>
public abstract class ISubscriptionContractTests {
  /// <summary>A live subscription from the transport under test.</summary>
  protected abstract Task<ISubscription> CreateSubscriptionAsync();

  [Test]
  public async Task ISubscription_Dispose_UnsubscribesAsync() {
    var subscription = await CreateSubscriptionAsync();
    _ = subscription.IsActive;

    subscription.Dispose();

    await Assert.That(subscription.IsActive).IsFalse();
  }

  [Test]
  public async Task ISubscription_Pause_SetsIsActiveFalseAsync() {
    var subscription = await CreateSubscriptionAsync();

    await subscription.PauseAsync();

    await Assert.That(subscription.IsActive).IsFalse();
  }

  [Test]
  public async Task ISubscription_Resume_SetsIsActiveTrueAsync() {
    var subscription = await CreateSubscriptionAsync();
    await subscription.PauseAsync();

    await subscription.ResumeAsync();

    await Assert.That(subscription.IsActive).IsTrue();
  }

  [Test]
  public async Task ISubscription_InitialState_IsActiveAsync() {
    var subscription = await CreateSubscriptionAsync();

    await Assert.That(subscription.IsActive).IsTrue();
  }

  [Test]
  public async Task ISubscription_DisposeMultipleTimes_DoesNotThrowAsync() {
    var subscription = await CreateSubscriptionAsync();

    // A repeated dispose must neither throw nor undo the first one.
    subscription.Dispose();
    subscription.Dispose();
    subscription.Dispose();

    await Assert.That(subscription.IsActive).IsFalse()
      .Because("a Dispose that toggled would resurrect a dead subscription");
  }

  [Test]
  public async Task ISubscription_PauseWhenPaused_DoesNotThrowAsync() {
    var subscription = await CreateSubscriptionAsync();
    await subscription.PauseAsync();

    await subscription.PauseAsync();

    await Assert.That(subscription.IsActive).IsFalse()
      .Because("pausing an already-paused subscription must not toggle it back to active");
  }

  [Test]
  public async Task ISubscription_ResumeWhenActive_DoesNotThrowAsync() {
    var subscription = await CreateSubscriptionAsync();

    await subscription.ResumeAsync();

    await Assert.That(subscription.IsActive).IsTrue()
      .Because("resuming an already-active subscription must leave it active");
  }
}

/// <summary>The contract, run against the in-process transport's subscription.</summary>
[InheritsTests]
public sealed class InProcessSubscriptionContractTests : ISubscriptionContractTests {
  protected override async Task<ISubscription> CreateSubscriptionAsync() {
    var transport = new InProcessTransport();
    return await transport.SubscribeBatchAsync(
      static (_, _) => Task.CompletedTask,
      new TransportDestination("contract-topic"),
      new TransportBatchOptions { BatchSize = 1, SlideMs = 10, MaxWaitMs = 100 });
  }
}
