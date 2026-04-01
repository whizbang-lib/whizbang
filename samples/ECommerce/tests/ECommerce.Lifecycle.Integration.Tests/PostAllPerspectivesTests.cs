using System.Diagnostics.CodeAnalysis;
using ECommerce.Integration.Tests.Fixtures;
using ECommerce.Lifecycle.Integration.Tests.Domain;
using ECommerce.Lifecycle.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace ECommerce.Lifecycle.Integration.Tests;

/// <summary>
/// Integration tests verifying PostAllPerspectivesDetached fires exactly once per event
/// when perspectives are processed across multiple batch cycles.
/// Isolated in its own assembly to avoid ServiceRegistrationCallbacks static contamination.
/// </summary>
[Category("Integration")]
[Category("Lifecycle")]
[NotInParallel("LifecycleRabbitMQ")]
public class PostAllPerspectivesTests {
  private static LifecycleIntegrationFixture? _fixture;

  [Before(Test)]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task SetupAsync() {
    await SharedRabbitMqFixtureSource.InitializeAsync();

    var postgresConnection = SharedRabbitMqFixtureSource.GetPerTestDatabaseConnectionString();

    _fixture = new LifecycleIntegrationFixture(
      SharedRabbitMqFixtureSource.RabbitMqConnectionString,
      postgresConnection,
      SharedRabbitMqFixtureSource.ManagementApiUri,
      testId: Guid.NewGuid().ToString("N")[..12]);
    await _fixture.InitializeAsync();
  }

  [After(Test)]
  public async Task CleanupAsync() {
    if (_fixture != null) {
      await _fixture.DisposeAsync();
      _fixture = null;
    }
  }

  /// <summary>
  /// Verifies that PostAllPerspectivesDetached fires exactly once per event
  /// when perspectives are processed across multiple batch cycles.
  /// 5 mock perspectives handle MockBatchTestEvent + 50 noise events flood the stream.
  /// With PerspectiveBatchSize=1, each perspective is claimed in a separate batch cycle.
  /// Bug: perspectivesPerStream only includes perspectives from current batch,
  /// so PostAllPerspectivesDetached fires once per batch cycle instead of once total.
  /// </summary>
  [Test]
  [Timeout(120_000)]
  public async Task PostAllPerspectivesDetached_WithManyPerspectivesAndEvents_FiresExactlyOncePerEventAsync(
    CancellationToken cancellationToken) {

    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    // Force batch size to 1 so each perspective is claimed in a separate batch cycle.
    var workerOptions = fixture.Host.Services.GetRequiredService<IOptionsMonitor<PerspectiveWorkerOptions>>();
    workerOptions.CurrentValue.PerspectiveBatchSize = 1;

    var command = new MockBatchTestCommand {
      StreamId = Guid.NewGuid(),
      NoiseEventCount = 50
    };

    // Register PostAllPerspectivesDetached receptor to count invocations
    var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var receptor = new GenericLifecycleCompletionReceptor<MockBatchTestEvent>(completionSource);

    var registry = fixture.Host.Services.GetRequiredService<IReceptorRegistry>();
    registry.Register<MockBatchTestEvent>(receptor, LifecycleStage.PostAllPerspectivesDetached);

    try {
      // Wait for all 5 mock perspectives to complete
      using var waiter = fixture.CreatePerspectiveWaiter<MockBatchTestEvent>(perspectives: 5);

      await fixture.Dispatcher.SendAsync(command);
      await waiter.WaitAsync(timeoutMilliseconds: 60_000);

      // Give time for any additional PostAllPerspectives firings from subsequent batches
      await Task.Delay(5000, cancellationToken);

      // Assert: PostAllPerspectivesDetached should fire ONCE, not 5 times (one per perspective)
      await Assert.That(receptor.InvocationCount).IsEqualTo(1);
    } finally {
      registry.Unregister<MockBatchTestEvent>(receptor, LifecycleStage.PostAllPerspectivesDetached);
      workerOptions.CurrentValue.PerspectiveBatchSize = 100;
    }
  }
}
