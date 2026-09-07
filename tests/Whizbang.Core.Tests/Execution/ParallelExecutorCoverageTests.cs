using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Execution;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Execution;

/// <summary>
/// Coverage-round tests for <see cref="ParallelExecutor.DisposeAsync"/>, which the
/// behavior/contract suite in Whizbang.Execution.Tests never calls.
/// </summary>
/// <tests>src/Whizbang.Core/Execution/ParallelExecutor.cs</tests>
[Category("Execution")]
public class ParallelExecutorCoverageTests {
  private static PolicyContext _createTestContext() => null!;

  private static MessageEnvelope<TestMessage> _createTestEnvelope() {
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New()
    });
    return envelope;
  }

  private sealed record TestMessage(string Content);

  // A DisposeAsync that forgot to stop the executor would leave callers free to keep
  // scheduling work against semaphore slots this instance no longer owns -- ExecuteAsync
  // must reject new work the same way it does after an explicit StopAsync.
  [Test]
  public async Task DisposeAsync_WhenRunning_StopsExecutorAsync() {
    // Arrange
    var executor = new ParallelExecutor(maxConcurrency: 2);
    await executor.StartAsync();
    var envelope = _createTestEnvelope();
    var context = _createTestContext();
    var result = await executor.ExecuteAsync<int>(
      envelope,
      (env, ctx) => ValueTask.FromResult(7),
      context
    );
    await Assert.That(result).IsEqualTo(7);

    // Act
    await executor.DisposeAsync();

    // Assert - the underlying StopAsync ran, so new work is rejected
    await Assert.That(async () => await executor.ExecuteAsync<int>(
      envelope,
      (env, ctx) => ValueTask.FromResult(0),
      context
    )).Throws<InvalidOperationException>()
      .Because("Dispose must stop the executor, not merely release its own resources");
  }

  // IAsyncDisposable requires Dispose to be safe to call more than once. If the _disposed
  // guard regressed, a second call would re-run StopAsync and re-dispose the semaphore, and
  // any future non-idempotent cleanup added to this method would break silently.
  [Test]
  public async Task DisposeAsync_CalledTwice_IsIdempotentAsync() {
    // Arrange
    var executor = new ParallelExecutor(maxConcurrency: 2);
    await executor.StartAsync();

    // Act - the second call must hit the _disposed early-return guard, not repeat the work
    await executor.DisposeAsync();
    await executor.DisposeAsync();

    // Assert - reaching here without exception proves idempotency
    await Assert.That(executor.Name).IsEqualTo("Parallel(max:2)");
  }
}
