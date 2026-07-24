using Whizbang.Core.Messaging;
using Whizbang.Testing.Lifecycle;
using Whizbang.Testing.Tests.TestSupport;

namespace Whizbang.Testing.Tests.Lifecycle;

/// <summary>
/// Tests for <see cref="MultiHostPerspectiveAwaiter{TEvent}"/> and the
/// <see cref="PerspectiveAwaiter"/> factory.
/// </summary>
public class MultiHostPerspectiveAwaiterTests {
  private static readonly TimeSpan _longTimeout = TimeSpan.FromSeconds(30);

  private sealed class PerspectiveA;

  private sealed class PerspectiveB;

  private static (FakeReceptorRegistry Registry, FakeHost Host) _makeHost() {
    var registry = new FakeReceptorRegistry();
    return (registry, new FakeHost(registry));
  }

  private static async Task _invokeWithPerspectiveAsync(FakeReceptorRegistry registry, Type perspectiveType) {
    var receptor = registry.GetSingleReceptor<TestEvent>();
    ((IAcceptsLifecycleContext)receptor).SetLifecycleContext(new FakeLifecycleContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      PerspectiveType = perspectiveType
    });
    await receptor.HandleAsync(new TestEvent("evt"));
  }

  private static async Task _invokeWithoutContextAsync(FakeReceptorRegistry registry) {
    var receptor = registry.GetSingleReceptor<TestEvent>();
    await receptor.HandleAsync(new TestEvent("evt"));
  }

  [Test]
  public async Task Ctor_NullConfigs_ThrowsAsync() {
    var ex = Assert.Throws<ArgumentNullException>(
      () => _ = new MultiHostPerspectiveAwaiter<TestEvent>(null!));

    await Assert.That(ex!.ParamName).IsEqualTo("hostConfigs");
  }

  [Test]
  public async Task Ctor_ZeroExpectedPerspectives_HostIsSkippedAsync() {
    var (registry, host) = _makeHost();

    using var awaiter = new MultiHostPerspectiveAwaiter<TestEvent>((host, 0));

    await Assert.That(registry.Registered.Count).IsEqualTo(0);
    await Assert.That(awaiter.AwaiterId).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task WaitAsync_NoRegistrations_ReturnsImmediatelyAsync() {
    var (_, host) = _makeHost();
    using var awaiter = new MultiHostPerspectiveAwaiter<TestEvent>((host, 0));

    // Zero timeout: only passes because there is genuinely nothing to wait for.
    await awaiter.WaitAsync(TimeSpan.Zero);
  }

  [Test]
  public async Task Ctor_PositiveExpectedPerspectives_RegistersAtPostPerspectiveInlineAsync() {
    var (registry, host) = _makeHost();

    using var awaiter = new MultiHostPerspectiveAwaiter<TestEvent>((host, 1));

    await Assert.That(registry.Registered.Count).IsEqualTo(1);
    await Assert.That(registry.Registered[0].Stage).IsEqualTo(LifecycleStage.PostPerspectiveInline);
    await Assert.That(registry.Registered[0].MessageType).IsEqualTo(typeof(TestEvent));
  }

  [Test]
  public async Task WaitAsync_DistinctPerspectivesReachExpectedCount_CompletesAsync() {
    var (registry, host) = _makeHost();
    using var awaiter = new MultiHostPerspectiveAwaiter<TestEvent>((host, 2));

    await _invokeWithPerspectiveAsync(registry, typeof(PerspectiveA));
    await _invokeWithPerspectiveAsync(registry, typeof(PerspectiveB));

    await awaiter.WaitAsync(_longTimeout);
  }

  [Test]
  public async Task WaitAsync_NullContextInvocations_EachCountUniqueAsync() {
    var (registry, host) = _makeHost();
    using var awaiter = new MultiHostPerspectiveAwaiter<TestEvent>((host, 2));

    // Without a lifecycle context each invocation gets a unique "unknown-{guid}" key.
    await _invokeWithoutContextAsync(registry);
    await _invokeWithoutContextAsync(registry);

    await awaiter.WaitAsync(_longTimeout);
  }

  [Test]
  public async Task WaitAsync_DuplicatePerspective_IsDeduplicated_TimesOutWithStatusAsync() {
    var (registry, host) = _makeHost();
    using var awaiter = new MultiHostPerspectiveAwaiter<TestEvent>((host, 2));

    await _invokeWithPerspectiveAsync(registry, typeof(PerspectiveA));
    await _invokeWithPerspectiveAsync(registry, typeof(PerspectiveA));

    var ex = await Assert.ThrowsAsync<TimeoutException>(
      async () => await awaiter.WaitAsync(TimeSpan.Zero));

    await Assert.That(ex!.Message).Contains("Not all perspectives completed");
    await Assert.That(ex.Message).Contains("1/2");
  }

  [Test]
  public async Task WaitAsync_TwoHosts_BothMustCompleteAsync() {
    var (registryA, hostA) = _makeHost();
    var (registryB, hostB) = _makeHost();
    using var awaiter = new MultiHostPerspectiveAwaiter<TestEvent>((hostA, 1), (hostB, 1));

    await _invokeWithPerspectiveAsync(registryA, typeof(PerspectiveA));

    // Only host A completed - wait must time out.
    await Assert.ThrowsAsync<TimeoutException>(async () => await awaiter.WaitAsync(TimeSpan.Zero));

    await _invokeWithPerspectiveAsync(registryB, typeof(PerspectiveB));

    await awaiter.WaitAsync(_longTimeout);
  }

  [Test]
  public async Task WaitAsync_MillisecondOverload_CompletesWhenSignaledAsync() {
    var (registry, host) = _makeHost();
    using var awaiter = new MultiHostPerspectiveAwaiter<TestEvent>((host, 1));

    await _invokeWithPerspectiveAsync(registry, typeof(PerspectiveA));

    await awaiter.WaitAsync(timeoutMilliseconds: 30_000);
  }

  [Test]
  public async Task WaitAsync_PreCancelledToken_PropagatesCancellationAsync() {
    var (_, host) = _makeHost();
    using var awaiter = new MultiHostPerspectiveAwaiter<TestEvent>((host, 1));
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    await Assert.ThrowsAsync<TaskCanceledException>(
      async () => await awaiter.WaitAsync(_longTimeout, cts.Token));
  }

  [Test]
  public async Task Dispose_UnregistersFromAllHosts_AndIsIdempotentAsync() {
    var (registryA, hostA) = _makeHost();
    var (registryB, hostB) = _makeHost();
    var awaiter = new MultiHostPerspectiveAwaiter<TestEvent>((hostA, 1), (hostB, 2));

    awaiter.Dispose();
    awaiter.Dispose();

    await Assert.That(registryA.Registered.Count).IsEqualTo(0);
    await Assert.That(registryB.Registered.Count).IsEqualTo(0);
    await Assert.That(registryA.Unregistered.Count).IsEqualTo(1);
    await Assert.That(registryB.Unregistered.Count).IsEqualTo(1);
  }

  [Test]
  public async Task Factory_ForHosts_CreatesRegisteredAwaiterAsync() {
    var (registry, host) = _makeHost();

    using var awaiter = PerspectiveAwaiter.ForHosts<TestEvent>((host, 1));

    await Assert.That(registry.Registered.Count).IsEqualTo(1);
  }

  [Test]
  public async Task Factory_ForInventoryAndBff_RegistersOnBothHostsAsync() {
    var (inventoryRegistry, inventoryHost) = _makeHost();
    var (bffRegistry, bffHost) = _makeHost();

    using var awaiter = PerspectiveAwaiter.ForInventoryAndBff<TestEvent>(inventoryHost, 1, bffHost, 1);

    await Assert.That(inventoryRegistry.Registered.Count).IsEqualTo(1);
    await Assert.That(bffRegistry.Registered.Count).IsEqualTo(1);
  }
}
