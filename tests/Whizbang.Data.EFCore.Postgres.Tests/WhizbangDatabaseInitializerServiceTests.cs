using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for WhizbangDatabaseInitializerService hosted service.
/// Verifies delegation to DbContextInitializationRegistry.
/// </summary>
[NotInParallel("DbContextInitializationRegistry")]
public class WhizbangDatabaseInitializerServiceTests {
  [Before(Test)]
  public void ResetStaticState() {
    // Reset DbContextInitializationRegistry static state
    var initializersField = typeof(DbContextInitializationRegistry)
        .GetField("_initializers", BindingFlags.Static | BindingFlags.NonPublic)!;
    var list = (System.Collections.IList)initializersField.GetValue(null)!;
    list.Clear();

    var initializedField = typeof(DbContextInitializationRegistry)
        .GetField("_initialized", BindingFlags.Static | BindingFlags.NonPublic)!;
    initializedField.SetValue(null, 0);
  }

  [Test]
  public async Task StartAsync_CallsInitializeAllAsyncAsync() {
    // Arrange
    var callbackInvoked = false;
    DbContextInitializationRegistry.Register<FakeInitDbContext>(
        (_, _, _) => { callbackInvoked = true; return Task.CompletedTask; });

    var sp = new FakeServiceProvider();
    var logger = NullLogger<WhizbangDatabaseInitializerService>.Instance;
    var gate = new SchemaReadyGate();
    var claimOptions = Options.Create(new ClaimWorkerOptions());
    var service = new WhizbangDatabaseInitializerService(sp, gate, claimOptions, logger);

    // Act
    await service.StartAsync(CancellationToken.None);

    // Assert — the registered callback was invoked via InitializeAllAsync
    await Assert.That(callbackInvoked).IsTrue();
    // Assert — the schema-ready gate has been opened so workers can proceed.
    await Assert.That(gate.IsReady).IsTrue();
  }

  [Test]
  public async Task StopAsync_ReturnsCompletedTaskAsync() {
    // Arrange
    var sp = new FakeServiceProvider();
    var logger = NullLogger<WhizbangDatabaseInitializerService>.Instance;
    var gate = new SchemaReadyGate();
    var claimOptions = Options.Create(new ClaimWorkerOptions());
    var service = new WhizbangDatabaseInitializerService(sp, gate, claimOptions, logger);

    // Act
    var task = service.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(task.IsCompleted).IsTrue();
  }

  [Test]
  public async Task StartAsync_CallsRecomputePartitions_BeforeMarkingGateReadyAsync() {
    // Arrange
    var coordinatorCalled = false;
    var gateReadyAtCallTime = false;

    var coordinator = new FakeCoordinator((_, _) => {
      coordinatorCalled = true;
      return Task.FromResult(new PartitionRecomputeResult { InboxRowsRecomputed = 7 });
    });

    var sp = new FakeServiceProvider(coordinator);
    var gate = new SchemaReadyGate();
    var claimOptions = Options.Create(new ClaimWorkerOptions { PartitionCount = 12345 });
    var service = new WhizbangDatabaseInitializerService(sp, gate, claimOptions, NullLogger<WhizbangDatabaseInitializerService>.Instance);

    // Capture gate state at the moment recompute is invoked. If the production code
    // calls MarkReady BEFORE recompute (the regression we want to catch), this captures true.
    coordinator.OnInvoked = () => gateReadyAtCallTime = gate.IsReady;

    // Act
    await service.StartAsync(CancellationToken.None);

    // Assert
    await Assert.That(coordinatorCalled).IsTrue();
    await Assert.That(coordinator.LastPartitionCount).IsEqualTo(12345);
    await Assert.That(gateReadyAtCallTime).IsFalse();   // recompute happened BEFORE MarkReady
    await Assert.That(gate.IsReady).IsTrue();           // and gate IS open by the time StartAsync returns
  }

  [Test]
  public async Task StartAsync_RecomputeThrows_StillMarksGateReadyAsync() {
    // Arrange
    var coordinator = new FakeCoordinator((_, _) => throw new InvalidOperationException("simulated recompute failure"));
    var sp = new FakeServiceProvider(coordinator);
    var gate = new SchemaReadyGate();
    var claimOptions = Options.Create(new ClaimWorkerOptions());
    var service = new WhizbangDatabaseInitializerService(sp, gate, claimOptions, NullLogger<WhizbangDatabaseInitializerService>.Instance);

    // Act — must not throw, must mark ready
    await service.StartAsync(CancellationToken.None);

    // Assert
    await Assert.That(gate.IsReady).IsTrue();
  }

  [Test]
  public async Task StartAsync_NoCoordinatorRegistered_StillMarksGateReadyAsync() {
    // Arrange — service provider returns null for IWorkCoordinator (no driver registered).
    // This is the test-fixture scenario where the EFCore Postgres driver isn't wired but the
    // initializer still runs to flip the gate.
    var sp = new FakeServiceProvider(coordinator: null);
    var gate = new SchemaReadyGate();
    var claimOptions = Options.Create(new ClaimWorkerOptions());
    var service = new WhizbangDatabaseInitializerService(sp, gate, claimOptions, NullLogger<WhizbangDatabaseInitializerService>.Instance);

    // Act
    await service.StartAsync(CancellationToken.None);

    // Assert
    await Assert.That(gate.IsReady).IsTrue();
  }

  private sealed class FakeInitDbContext;

  private sealed class FakeServiceProvider(FakeCoordinator? coordinator = null) : IServiceProvider {
    private readonly FakeCoordinator? _coordinator = coordinator;
    public FakeServiceProvider() : this(coordinator: null) { }
    public object? GetService(Type serviceType) {
      if (serviceType == typeof(IWorkCoordinator)) {
        return _coordinator;
      }
      // Microsoft.Extensions.DependencyInjection.IServiceScopeFactory is needed by
      // CreateAsyncScope() — return a minimal fake.
      if (serviceType == typeof(Microsoft.Extensions.DependencyInjection.IServiceScopeFactory)) {
        return new FakeScopeFactory(this);
      }
      return null;
    }
  }

  private sealed class FakeScopeFactory(IServiceProvider sp) : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory {
    public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() => new FakeScope(sp);
  }

  private sealed class FakeScope(IServiceProvider sp) : Microsoft.Extensions.DependencyInjection.IServiceScope {
    public IServiceProvider ServiceProvider => sp;
    public void Dispose() { }
  }

  private sealed class FakeCoordinator(Func<int, CancellationToken, Task<PartitionRecomputeResult>> handler) : IWorkCoordinator {
    public Action? OnInvoked { get; set; }
    public int LastPartitionCount { get; private set; } = -1;

    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) {
      LastPartitionCount = partitionCount;
      OnInvoked?.Invoke();
      return handler(partitionCount, cancellationToken);
    }

    // Default-throws / no-op for everything else — the test only exercises recompute.
    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default)
      => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken cancellationToken = default) => Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }
}
