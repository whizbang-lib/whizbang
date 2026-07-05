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

    // Both completion sources are pre-completed, so any timeout works instantly.
    await waiter.WaitAsync(timeoutMilliseconds: 1);
  }

  [Test]
  public async Task WaitAsync_BothHostsComplete_ReturnsAsync() {
    var inventory = new FakeReceptorRegistry();
    var bff = new FakeReceptorRegistry();
    using var waiter = new PerspectiveCompletionWaiter<TestEvent>(inventory, bff, 1, 1);

    await inventory.GetSingleReceptor<TestEvent>().HandleAsync(new TestEvent("evt"));
    await bff.GetSingleReceptor<TestEvent>().HandleAsync(new TestEvent("evt"));

    await waiter.WaitAsync(timeoutMilliseconds: 30_000);
  }

  [Test]
  public async Task WaitAsync_ZeroBffPerspectives_OnlyInventoryMustCompleteAsync() {
    var inventory = new FakeReceptorRegistry();
    var bff = new FakeReceptorRegistry();
    using var waiter = new PerspectiveCompletionWaiter<TestEvent>(inventory, bff, 1, 0);

    await inventory.GetSingleReceptor<TestEvent>().HandleAsync(new TestEvent("evt"));

    await waiter.WaitAsync(timeoutMilliseconds: 30_000);
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
