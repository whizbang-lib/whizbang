using System.Collections.Concurrent;
using Whizbang.Core.Messaging;
using Whizbang.Testing.Lifecycle;
using Whizbang.Testing.Tests.TestSupport;

namespace Whizbang.Testing.Tests.Lifecycle;

/// <summary>
/// Tests for <see cref="CountingPerspectiveReceptor{TEvent}"/> - per (perspective, stream)
/// completion counting and deduplication.
/// </summary>
public class CountingPerspectiveReceptorTests {
  private sealed class InventoryPerspective;

  private static (CountingPerspectiveReceptor<TestEvent> Receptor,
                  TaskCompletionSource<bool> Tcs,
                  ConcurrentDictionary<string, byte> Completed) _makeReceptor(int expected) {
    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var completed = new ConcurrentDictionary<string, byte>();
    var receptor = new CountingPerspectiveReceptor<TestEvent>(tcs, completed, expected);
    return (receptor, tcs, completed);
  }

  [Test]
  public async Task Ctor_NullCompletionSource_ThrowsAsync() {
    var ex = Assert.Throws<ArgumentNullException>(() => _ = new CountingPerspectiveReceptor<TestEvent>(
      null!, new ConcurrentDictionary<string, byte>(), 1));

    await Assert.That(ex!.ParamName).IsEqualTo("completionSource");
  }

  [Test]
  public async Task Ctor_NullCompletedDictionary_ThrowsAsync() {
    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    var ex = Assert.Throws<ArgumentNullException>(() => _ = new CountingPerspectiveReceptor<TestEvent>(
      tcs, null!, 1));

    await Assert.That(ex!.ParamName).IsEqualTo("completedPerspectives");
  }

  [Test]
  public async Task HandleAsync_NoContext_UsesUnknownKeyAndSignalsAtExpectedCountAsync() {
    var (receptor, tcs, completed) = _makeReceptor(expected: 1);

    await receptor.HandleAsync(new TestEvent("evt"));

    await Assert.That(tcs.Task.IsCompleted).IsTrue();
    await Assert.That(completed.ContainsKey("Unknown:Unknown")).IsTrue();
  }

  [Test]
  public async Task HandleAsync_TracksPerspectiveAndStreamInKeyAsync() {
    var (receptor, tcs, completed) = _makeReceptor(expected: 1);
    var streamId = Guid.NewGuid();
    receptor.SetLifecycleContext(new FakeLifecycleContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      PerspectiveType = typeof(InventoryPerspective),
      StreamId = streamId
    });

    await receptor.HandleAsync(new TestEvent("evt"));

    await Assert.That(tcs.Task.IsCompleted).IsTrue();
    await Assert.That(completed.ContainsKey($"{nameof(InventoryPerspective)}:{streamId}")).IsTrue();
  }

  [Test]
  public async Task HandleAsync_DistinctStreams_CountSeparatelyAsync() {
    var (receptor, tcs, completed) = _makeReceptor(expected: 2);
    var streamA = Guid.NewGuid();
    var streamB = Guid.NewGuid();

    receptor.SetLifecycleContext(new FakeLifecycleContext {
      PerspectiveType = typeof(InventoryPerspective),
      StreamId = streamA
    });
    await receptor.HandleAsync(new TestEvent("evt-a"));
    await Assert.That(tcs.Task.IsCompleted).IsFalse();

    receptor.SetLifecycleContext(new FakeLifecycleContext {
      PerspectiveType = typeof(InventoryPerspective),
      StreamId = streamB
    });
    await receptor.HandleAsync(new TestEvent("evt-b"));

    await Assert.That(tcs.Task.IsCompleted).IsTrue();
    await Assert.That(completed.Count).IsEqualTo(2);
  }

  [Test]
  public async Task HandleAsync_DuplicateInvocation_IsNotDoubleCountedAsync() {
    var (receptor, tcs, completed) = _makeReceptor(expected: 2);
    var streamId = Guid.NewGuid();
    receptor.SetLifecycleContext(new FakeLifecycleContext {
      PerspectiveType = typeof(InventoryPerspective),
      StreamId = streamId
    });

    await receptor.HandleAsync(new TestEvent("evt"));
    await receptor.HandleAsync(new TestEvent("evt"));

    await Assert.That(tcs.Task.IsCompleted).IsFalse();
    await Assert.That(completed.Count).IsEqualTo(1);
  }
}
