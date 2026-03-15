using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Coverage tests for Dispatcher perspective sync early return paths:
/// - _waitForPerspectivesIfNeededAsync: line 296 (null event completion awaiter)
/// - _waitForPerspectivesIfNeededAsync: line 302 (null scoped tracker)
/// </summary>
[Category("Dispatcher")]
[Category("Coverage")]
public class DispatcherPerspectiveSyncCoverageTests {

  public record TestCmd(string Data);
  public record TestResult(Guid Id);

  [DefaultRouting(DispatchMode.Local)]
  public record TestEvt([property: StreamId] Guid Id) : IEvent;

  // ========================================
  // _waitForPerspectivesIfNeededAsync coverage
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_WithWaitForPerspectives_NoEventCompletionAwaiter_ReturnsNormallyAsync() {
    // Arrange - WaitForPerspectives=true but no IEventCompletionAwaiter (line 296)
    var services = new ServiceCollection();
    var provider = services.BuildServiceProvider();

    var result = new TestResult(Guid.NewGuid());
    var dispatcher = new CoverageTestDispatcher(
      provider,
      invoker: _ => new ValueTask<object>(result)
    );

    var options = new DispatchOptions {
      WaitForPerspectives = true,
      PerspectiveWaitTimeout = TimeSpan.FromMilliseconds(100)
    };

    // Act - Should complete normally (event completion awaiter is null, early return)
    var actual = await dispatcher.LocalInvokeAsync<TestResult>(new TestCmd("test"), options: options);

    // Assert
    await Assert.That(actual).IsNotNull();
    await Assert.That(actual.Id).IsEqualTo(result.Id);
  }

  [Test]
  public async Task LocalInvokeAsync_WithWaitForPerspectives_NoScopedTracker_ReturnsNormallyAsync() {
    // Arrange - WaitForPerspectives=true, has event completion awaiter but no scoped tracker (line 302)
    var services = new ServiceCollection();
    services.AddSingleton<IEventCompletionAwaiter>(new StubEventCompletionAwaiter());
    var provider = services.BuildServiceProvider();

    var result = new TestResult(Guid.NewGuid());
    var dispatcher = new CoverageTestDispatcher(
      provider,
      invoker: _ => new ValueTask<object>(result)
    );

    // Make sure ScopedEventTrackerAccessor has no tracker
    ScopedEventTrackerAccessor.CurrentTracker = null;

    var options = new DispatchOptions {
      WaitForPerspectives = true,
      PerspectiveWaitTimeout = TimeSpan.FromMilliseconds(100)
    };

    // Act - Should complete normally (scoped tracker is null, early return)
    var actual = await dispatcher.LocalInvokeAsync<TestResult>(new TestCmd("test"), options: options);

    // Assert
    await Assert.That(actual).IsNotNull();
    await Assert.That(actual.Id).IsEqualTo(result.Id);
  }

  // ========================================
  // Test Dispatcher
  // ========================================

  private sealed class CoverageTestDispatcher : Core.Dispatcher {
    private readonly ReceptorInvoker<object>? _invoker;

    public CoverageTestDispatcher(
      IServiceProvider sp,
      ReceptorInvoker<object>? invoker = null
    ) : base(sp, new StubServiceInstanceProvider()) {
      _invoker = invoker;
    }

    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) {
      if (_invoker != null && messageType == typeof(TestCmd)) {
        return msg => {
          var task = _invoker(msg);
          return new ValueTask<TResult>(task.AsTask().ContinueWith(t => (TResult)t.Result));
        };
      }
      return null;
    }

    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) => null;
    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) =>
      _ => Task.CompletedTask;
    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) => null;
    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) => null;
    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) => null;
    protected override DispatchMode? GetReceptorDefaultRouting(Type messageType) => null;
  }

  // ========================================
  // Stubs
  // ========================================

  private sealed class StubServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName => "Test";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class StubEventCompletionAwaiter : IEventCompletionAwaiter {
    public Guid AwaiterId { get; } = Guid.NewGuid();
    public string AwaiterName => "StubAwaiter";
    public string AwaiterType => "Stub";

    public Task<bool> WaitForEventsAsync(
      IReadOnlyList<Guid> eventIds,
      TimeSpan timeout,
      CancellationToken cancellationToken = default) =>
      Task.FromResult(true);

    public bool AreEventsFullyProcessed(IReadOnlyList<Guid> eventIds) => true;
    public void SignalEventCompleted(Guid eventId) { }
    public void TrackEvent(Guid eventId, int expectedPerspectiveCount) { }
  }
}
