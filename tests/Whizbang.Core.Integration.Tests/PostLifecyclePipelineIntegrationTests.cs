#pragma warning disable CA1707

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Integration.Tests.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Integration.Tests;

/// <summary>
/// Integration tests proving PostLifecycle fires at the end of each pipeline path.
/// Uses the REAL Dispatcher code path — not mocks or simulations.
/// Each test dispatches a real command and verifies PostLifecycle receptors fire.
/// </summary>
/// <docs>fundamentals/lifecycle/lifecycle-coordinator</docs>
[Category("Integration")]
public class PostLifecyclePipelineIntegrationTests {

  // ════════════════════════════════════════════════════════════════════════
  //  Test messages
  // ════════════════════════════════════════════════════════════════════════

  public sealed record PostLifecycleTestCommand(string Name) : ICommand;
  public sealed record PostLifecycleTestEvent(Guid Id, string Name) : IMessage;

  public class PostLifecycleTestCommandHandler : IReceptor<PostLifecycleTestCommand, PostLifecycleTestEvent> {
    public ValueTask<PostLifecycleTestEvent> HandleAsync(PostLifecycleTestCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new PostLifecycleTestEvent(Guid.NewGuid(), message.Name));
    }
  }

  // ════════════════════════════════════════════════════════════════════════
  //  Tracking receptor — records PostLifecycle firings
  // ════════════════════════════════════════════════════════════════════════

  public sealed class PostLifecycleTracker : IReceptor<PostLifecycleTestCommand> {
    private int _fireCount;
    public int FireCount => _fireCount;

    public ValueTask HandleAsync(PostLifecycleTestCommand message, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _fireCount);
      return ValueTask.CompletedTask;
    }
  }

  // ════════════════════════════════════════════════════════════════════════
  //  Dispatcher — Local dispatch fires PostLifecycle
  // ════════════════════════════════════════════════════════════════════════

  [Test]
  public async Task Dispatcher_LocalInvoke_FiresPostLifecycleAsync() {
    // Arrange — real dispatcher with runtime-registered PostLifecycle receptor
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var provider = services.BuildServiceProvider();

    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var registry = provider.GetRequiredService<IReceptorRegistry>();

    // Register a PostLifecycle receptor at runtime
    var tracker = new PostLifecycleTracker();
    registry.Register<PostLifecycleTestCommand>(tracker, LifecycleStage.PostLifecycleAsync);

    try {
      // Act — dispatch a command through the REAL dispatcher
      var result = await dispatcher.LocalInvokeAsync<PostLifecycleTestEvent>(new PostLifecycleTestCommand("lifecycle-test"));

      // Assert — the command processed
      await Assert.That(result).IsNotNull();
      await Assert.That(result!.Name).IsEqualTo("lifecycle-test");

      // Assert — PostLifecycle fired through the real Dispatcher code path
      await Assert.That(tracker.FireCount).IsEqualTo(1)
        .Because("Dispatcher must fire PostLifecycleAsync after local dispatch completes");
    } finally {
      registry.Unregister<PostLifecycleTestCommand>(tracker, LifecycleStage.PostLifecycleAsync);
    }
  }

  [Test]
  public async Task Dispatcher_LocalInvoke_MultipleCommands_FiresPostLifecycleForEachAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var provider = services.BuildServiceProvider();

    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var registry = provider.GetRequiredService<IReceptorRegistry>();

    var tracker = new PostLifecycleTracker();
    registry.Register<PostLifecycleTestCommand>(tracker, LifecycleStage.PostLifecycleAsync);

    try {
      // Act — dispatch 5 commands
      for (int i = 0; i < 5; i++) {
        await dispatcher.LocalInvokeAsync<PostLifecycleTestEvent>(new PostLifecycleTestCommand($"cmd-{i}"));
      }

      // Assert — PostLifecycle fires once per command
      await Assert.That(tracker.FireCount).IsEqualTo(5)
        .Because("PostLifecycle must fire once per dispatched command");
    } finally {
      registry.Unregister<PostLifecycleTestCommand>(tracker, LifecycleStage.PostLifecycleAsync);
    }
  }
}
