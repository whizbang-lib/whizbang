using Whizbang.Core.Messaging;
using Whizbang.Testing.Lifecycle;
using Whizbang.Testing.Tests.TestSupport;

namespace Whizbang.Testing.Tests.Lifecycle;

/// <summary>
/// Tests for <see cref="LifecycleStageAwaiter{TMessage}"/> and the <see cref="LifecycleAwaiter"/>
/// factory. Receptors are captured from a fake registry and driven with explicit invocations.
/// </summary>
public class LifecycleStageAwaiterTests {
  private static readonly TimeSpan _longTimeout = TimeSpan.FromSeconds(30);

  private sealed class MarkerPerspective;

  private static (FakeReceptorRegistry Registry, FakeHost Host) _makeHost() {
    var registry = new FakeReceptorRegistry();
    return (registry, new FakeHost(registry));
  }

  private static async Task _invokeAsync(
    FakeReceptorRegistry registry,
    TestEvent message,
    FakeLifecycleContext? context) {
    var receptor = registry.GetSingleReceptor<TestEvent>();
    if (context is not null) {
      ((IAcceptsLifecycleContext)receptor).SetLifecycleContext(context);
    }
    await receptor.HandleAsync(message);
  }

  [Test]
  public async Task Ctor_NullHost_ThrowsAsync() {
    var ex = Assert.Throws<ArgumentNullException>(
      () => _ = new LifecycleStageAwaiter<TestEvent>(null!, LifecycleStage.PostPerspectiveInline));

    await Assert.That(ex!.ParamName).IsEqualTo("host");
  }

  [Test]
  public async Task Ctor_RegistersReceptorAtRequestedStageAsync() {
    var (registry, host) = _makeHost();

    using var awaiter = new LifecycleStageAwaiter<TestEvent>(host, LifecycleStage.PostPerspectiveInline);

    await Assert.That(registry.Registered.Count).IsEqualTo(1);
    await Assert.That(registry.Registered[0].MessageType).IsEqualTo(typeof(TestEvent));
    await Assert.That(registry.Registered[0].Stage).IsEqualTo(LifecycleStage.PostPerspectiveInline);
    await Assert.That(awaiter.AwaiterId).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task WaitAsync_ReceptorInvokedAtMatchingStage_CompletesWithMessageAsync() {
    var (registry, host) = _makeHost();
    using var awaiter = new LifecycleStageAwaiter<TestEvent>(host, LifecycleStage.PostPerspectiveInline);
    var message = new TestEvent("done");

    await _invokeAsync(registry, message, new FakeLifecycleContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline
    });

    var result = await awaiter.WaitAsync(_longTimeout);
    await Assert.That(result).IsEqualTo(message);
    await Assert.That(awaiter.InvocationCount).IsEqualTo(1);
    await Assert.That(awaiter.LastMessage).IsEqualTo(message);
  }

  [Test]
  public async Task WaitAsync_MessageFilterRejects_DoesNotCompleteAsync() {
    var (registry, host) = _makeHost();
    using var awaiter = new LifecycleStageAwaiter<TestEvent>(
      host, LifecycleStage.PostPerspectiveInline, messageFilter: m => m.Name == "wanted");

    await _invokeAsync(registry, new TestEvent("unwanted"), new FakeLifecycleContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline
    });

    await Assert.That(awaiter.InvocationCount).IsEqualTo(0);
    // LastMessage is recorded even for filtered messages (diagnostic surface).
    await Assert.That(awaiter.LastMessage).IsEqualTo(new TestEvent("unwanted"));
    var ex = await Assert.ThrowsAsync<TimeoutException>(
      async () => await awaiter.WaitAsync(TimeSpan.Zero));
    await Assert.That(ex!.Message).Contains("PostPerspectiveInline");
  }

  [Test]
  public async Task WaitAsync_MessageFilterAccepts_CompletesAsync() {
    var (registry, host) = _makeHost();
    using var awaiter = new LifecycleStageAwaiter<TestEvent>(
      host, LifecycleStage.PostPerspectiveInline, messageFilter: m => m.Name == "wanted");

    await _invokeAsync(registry, new TestEvent("wanted"), new FakeLifecycleContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline
    });

    var result = await awaiter.WaitAsync(_longTimeout);
    await Assert.That(result.Name).IsEqualTo("wanted");
  }

  [Test]
  public async Task WaitAsync_StageMismatch_DoesNotCompleteAsync() {
    var (registry, host) = _makeHost();
    using var awaiter = new LifecycleStageAwaiter<TestEvent>(host, LifecycleStage.PostPerspectiveInline);

    await _invokeAsync(registry, new TestEvent("wrong-stage"), new FakeLifecycleContext {
      CurrentStage = LifecycleStage.PrePerspectiveInline
    });

    await Assert.That(awaiter.InvocationCount).IsEqualTo(0);
    await Assert.ThrowsAsync<TimeoutException>(async () => await awaiter.WaitAsync(TimeSpan.Zero));
  }

  [Test]
  public async Task WaitAsync_NoLifecycleContext_DoesNotCompleteAsync() {
    var (registry, host) = _makeHost();
    using var awaiter = new LifecycleStageAwaiter<TestEvent>(host, LifecycleStage.PostPerspectiveInline);

    await _invokeAsync(registry, new TestEvent("no-context"), context: null);

    await Assert.That(awaiter.InvocationCount).IsEqualTo(0);
    await Assert.ThrowsAsync<TimeoutException>(async () => await awaiter.WaitAsync(TimeSpan.Zero));
  }

  [Test]
  public async Task WaitAsync_PerspectiveNameMismatch_DoesNotCompleteAsync() {
    var (registry, host) = _makeHost();
    using var awaiter = new LifecycleStageAwaiter<TestEvent>(
      host, LifecycleStage.PostPerspectiveInline, perspectiveName: nameof(MarkerPerspective));

    await _invokeAsync(registry, new TestEvent("other-perspective"), new FakeLifecycleContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      PerspectiveType = typeof(string)
    });

    await Assert.That(awaiter.InvocationCount).IsEqualTo(0);
    await Assert.ThrowsAsync<TimeoutException>(async () => await awaiter.WaitAsync(TimeSpan.Zero));
  }

  [Test]
  public async Task WaitAsync_PerspectiveNameMatch_CompletesAsync() {
    var (registry, host) = _makeHost();
    using var awaiter = new LifecycleStageAwaiter<TestEvent>(
      host, LifecycleStage.PostPerspectiveInline, perspectiveName: nameof(MarkerPerspective));

    await _invokeAsync(registry, new TestEvent("right-perspective"), new FakeLifecycleContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      PerspectiveType = typeof(MarkerPerspective)
    });

    var result = await awaiter.WaitAsync(_longTimeout);
    await Assert.That(result.Name).IsEqualTo("right-perspective");
  }

  [Test]
  public async Task WaitAsync_DistributeStage_InboxSource_IsSkippedByDefaultAsync() {
    var (registry, host) = _makeHost();
    using var awaiter = new LifecycleStageAwaiter<TestEvent>(host, LifecycleStage.PreDistributeInline);

    await _invokeAsync(registry, new TestEvent("from-inbox"), new FakeLifecycleContext {
      CurrentStage = LifecycleStage.PreDistributeInline,
      MessageSource = MessageSource.Inbox
    });

    await Assert.That(awaiter.InvocationCount).IsEqualTo(0);
    await Assert.ThrowsAsync<TimeoutException>(async () => await awaiter.WaitAsync(TimeSpan.Zero));
  }

  [Test]
  public async Task WaitAsync_DistributeStage_OutboxSource_CompletesAsync() {
    var (registry, host) = _makeHost();
    using var awaiter = new LifecycleStageAwaiter<TestEvent>(host, LifecycleStage.PreDistributeInline);

    await _invokeAsync(registry, new TestEvent("from-outbox"), new FakeLifecycleContext {
      CurrentStage = LifecycleStage.PreDistributeInline,
      MessageSource = MessageSource.Outbox
    });

    var result = await awaiter.WaitAsync(_longTimeout);
    await Assert.That(result.Name).IsEqualTo("from-outbox");
  }

  [Test]
  public async Task WaitAsync_DistributeStage_InboxSource_CountsWhenSkipDisabledAsync() {
    var (registry, host) = _makeHost();
    using var awaiter = new LifecycleStageAwaiter<TestEvent>(
      host, LifecycleStage.PreDistributeInline, skipInboxForDistributeStages: false);

    await _invokeAsync(registry, new TestEvent("inbox-counted"), new FakeLifecycleContext {
      CurrentStage = LifecycleStage.PreDistributeInline,
      MessageSource = MessageSource.Inbox
    });

    var result = await awaiter.WaitAsync(_longTimeout);
    await Assert.That(result.Name).IsEqualTo("inbox-counted");
    await Assert.That(awaiter.InvocationCount).IsEqualTo(1);
  }

  [Test]
  public async Task WaitAsync_MillisecondOverload_TimesOutWithStageInMessageAsync() {
    var (_, host) = _makeHost();
    using var awaiter = new LifecycleStageAwaiter<TestEvent>(host, LifecycleStage.ImmediateDetached);

    var ex = await Assert.ThrowsAsync<TimeoutException>(
      async () => await awaiter.WaitAsync(timeoutMilliseconds: 0));

    await Assert.That(ex!.Message).Contains("ImmediateDetached");
    await Assert.That(ex.Message).Contains("not completed within");
  }

  [Test]
  public async Task WaitAsync_PreCancelledToken_ThrowsOperationCanceledAsync() {
    var (_, host) = _makeHost();
    using var awaiter = new LifecycleStageAwaiter<TestEvent>(host, LifecycleStage.PostPerspectiveInline);
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    await Assert.ThrowsAsync<TaskCanceledException>(
      async () => await awaiter.WaitAsync(_longTimeout, cts.Token));
  }

  [Test]
  public async Task Dispose_UnregistersReceptor_AndIsIdempotentAsync() {
    var (registry, host) = _makeHost();
    var awaiter = new LifecycleStageAwaiter<TestEvent>(host, LifecycleStage.PostPerspectiveInline);

    awaiter.Dispose();
    awaiter.Dispose();

    await Assert.That(registry.Registered.Count).IsEqualTo(0);
    await Assert.That(registry.Unregistered.Count).IsEqualTo(1);
    await Assert.That(registry.Unregistered[0].Stage).IsEqualTo(LifecycleStage.PostPerspectiveInline);
  }

  [Test]
  public async Task Factory_For_RegistersAtGivenStageAsync() {
    var (registry, host) = _makeHost();

    using var awaiter = LifecycleAwaiter.For<TestEvent>(host, LifecycleStage.PostOutboxInline);

    await Assert.That(registry.Registered[0].Stage).IsEqualTo(LifecycleStage.PostOutboxInline);
  }

  [Test]
  public async Task Factory_ForPerspectiveCompletion_UsesPostPerspectiveInlineAsync() {
    var (registry, host) = _makeHost();

    using var awaiter = LifecycleAwaiter.ForPerspectiveCompletion<TestEvent>(host);

    await Assert.That(registry.Registered[0].Stage).IsEqualTo(LifecycleStage.PostPerspectiveInline);
  }

  [Test]
  public async Task Factory_ForPrePerspective_UsesPrePerspectiveInlineAsync() {
    var (registry, host) = _makeHost();

    using var awaiter = LifecycleAwaiter.ForPrePerspective<TestEvent>(host);

    await Assert.That(registry.Registered[0].Stage).IsEqualTo(LifecycleStage.PrePerspectiveInline);
  }

  [Test]
  public async Task Factory_ForImmediateDetached_UsesImmediateDetachedAsync() {
    var (registry, host) = _makeHost();

    using var awaiter = LifecycleAwaiter.ForImmediateDetached<TestCommand>(host);

    await Assert.That(registry.Registered[0].MessageType).IsEqualTo(typeof(TestCommand));
    await Assert.That(registry.Registered[0].Stage).IsEqualTo(LifecycleStage.ImmediateDetached);
  }

  [Test]
  public async Task Factory_ForPostDistribute_UsesPostDistributeInlineAsync() {
    var (registry, host) = _makeHost();

    using var awaiter = LifecycleAwaiter.ForPostDistribute<TestEvent>(host);

    await Assert.That(registry.Registered[0].Stage).IsEqualTo(LifecycleStage.PostDistributeInline);
  }

  [Test]
  public async Task Factory_ForPreDistribute_UsesPreDistributeInlineAsync() {
    var (registry, host) = _makeHost();

    using var awaiter = LifecycleAwaiter.ForPreDistribute<TestEvent>(host);

    await Assert.That(registry.Registered[0].Stage).IsEqualTo(LifecycleStage.PreDistributeInline);
  }

  [Test]
  public async Task Factory_ForPostOutbox_UsesPostOutboxInlineAsync() {
    var (registry, host) = _makeHost();

    using var awaiter = LifecycleAwaiter.ForPostOutbox<TestEvent>(host);

    await Assert.That(registry.Registered[0].Stage).IsEqualTo(LifecycleStage.PostOutboxInline);
  }

  [Test]
  public async Task Factory_ForPostInbox_UsesPostInboxInlineAsync() {
    var (registry, host) = _makeHost();

    using var awaiter = LifecycleAwaiter.ForPostInbox<TestEvent>(host);

    await Assert.That(registry.Registered[0].Stage).IsEqualTo(LifecycleStage.PostInboxInline);
  }
}
