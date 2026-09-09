using Whizbang.Core.Messaging;
using Whizbang.Testing.Lifecycle;
using Whizbang.Testing.Tests.TestSupport;

namespace Whizbang.Testing.Tests.Lifecycle;

/// <summary>
/// Tests for <see cref="PerspectiveCompletionWaiter{TEvent}"/> - two-registry perspective
/// completion waiting driven by explicit receptor invocations.
/// </summary>
public class PerspectiveCompletionWaiterTests {
  [Test]
  public async Task Ctor_NullInventoryRegistry_ThrowsAsync() {
    var ex = Assert.Throws<ArgumentNullException>(() => _ = new PerspectiveCompletionWaiter<TestEvent>(
      null!, new FakeReceptorRegistry(), 1, 1));

    await Assert.That(ex!.ParamName).IsEqualTo("inventoryRegistry");
  }

  [Test]
  public async Task Ctor_NullBffRegistry_ThrowsAsync() {
    var ex = Assert.Throws<ArgumentNullException>(() => _ = new PerspectiveCompletionWaiter<TestEvent>(
      new FakeReceptorRegistry(), null!, 1, 1));

    await Assert.That(ex!.ParamName).IsEqualTo("bffRegistry");
  }

  [Test]
  public async Task Ctor_RegistersReceptorsOnBothRegistriesAtPostPerspectiveInlineAsync() {
    var inventory = new FakeReceptorRegistry();
    var bff = new FakeReceptorRegistry();

    using var waiter = new PerspectiveCompletionWaiter<TestEvent>(inventory, bff, 1, 1);

    await Assert.That(inventory.Registered.Count).IsEqualTo(1);
    await Assert.That(bff.Registered.Count).IsEqualTo(1);
    await Assert.That(inventory.Registered[0].Stage).IsEqualTo(LifecycleStage.PostPerspectiveInline);
    await Assert.That(bff.Registered[0].Stage).IsEqualTo(LifecycleStage.PostPerspectiveInline);
  }

  [Test]
  public async Task WaitAsync_ZeroPerspectivesOnBothHosts_CompletesImmediatelyAsync() {
    var inventory = new FakeReceptorRegistry();
    var bff = new FakeReceptorRegistry();
    using var waiter = new PerspectiveCompletionWaiter<TestEvent>(inventory, bff, 0, 0);

    var wait = waiter.WaitAsync(timeoutMilliseconds: 1);

    // Both completion sources are pre-completed in the constructor, so the wait is already done
    // before it is awaited. A host that registers no perspectives must not make callers pay the
    // timeout — nothing is ever going to signal it.
    await Assert.That(wait.IsCompletedSuccessfully).IsTrue()
      .Because("expecting zero perspectives completes without waiting on anything");
    await wait;
  }

  [Test]
  public async Task WaitAsync_BothHostsComplete_ReturnsAsync() {
    var inventory = new FakeReceptorRegistry();
    var bff = new FakeReceptorRegistry();
    using var waiter = new PerspectiveCompletionWaiter<TestEvent>(inventory, bff, 1, 1);

    var wait = waiter.WaitAsync(timeoutMilliseconds: 30_000);
    await Assert.That(wait.IsCompleted).IsFalse()
      .Because("neither host has run its perspective yet");

    await inventory.GetSingleReceptor<TestEvent>().HandleAsync(new TestEvent("evt"));
    await Assert.That(wait.IsCompleted).IsFalse()
      .Because("returning here would let a test assert against the BFF's read model before it "
             + "was written — the waiter's whole job is that both hosts are done");

    await bff.GetSingleReceptor<TestEvent>().HandleAsync(new TestEvent("evt"));

    await wait;
  }

  [Test]
  public async Task WaitAsync_ZeroBffPerspectives_OnlyInventoryMustCompleteAsync() {
    var inventory = new FakeReceptorRegistry();
    var bff = new FakeReceptorRegistry();
    using var waiter = new PerspectiveCompletionWaiter<TestEvent>(inventory, bff, 1, 0);

    var wait = waiter.WaitAsync(timeoutMilliseconds: 30_000);
    await Assert.That(wait.IsCompleted).IsFalse()
      .Because("the inventory host still owes its one perspective");

    await inventory.GetSingleReceptor<TestEvent>().HandleAsync(new TestEvent("evt"));

    // The BFF receptor is never invoked: a host expecting zero perspectives is pre-completed, so
    // the wait returns on inventory alone rather than hanging until the timeout.
    await wait;
    await Assert.That(bff.Registered.Count).IsEqualTo(1)
      .Because("the BFF receptor stays registered and simply never fires — it is not required");
  }

  [Test]
  public async Task WaitAsync_PerspectivesNeverComplete_ThrowsTimeoutAsync() {
    var inventory = new FakeReceptorRegistry();
    var bff = new FakeReceptorRegistry();
    using var waiter = new PerspectiveCompletionWaiter<TestEvent>(inventory, bff, 1, 1);

    await Assert.ThrowsAsync<TimeoutException>(
      async () => await waiter.WaitAsync(timeoutMilliseconds: 0));
  }

  [Test]
  public async Task Dispose_UnregistersFromBothRegistriesAsync() {
    var inventory = new FakeReceptorRegistry();
    var bff = new FakeReceptorRegistry();
    var waiter = new PerspectiveCompletionWaiter<TestEvent>(inventory, bff, 1, 1);

    waiter.Dispose();

    await Assert.That(inventory.Registered.Count).IsEqualTo(0);
    await Assert.That(bff.Registered.Count).IsEqualTo(0);
    await Assert.That(inventory.Unregistered.Count).IsEqualTo(1);
    await Assert.That(bff.Unregistered.Count).IsEqualTo(1);
  }
}
