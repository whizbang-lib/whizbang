using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Observability;
using Whizbang.Core.Tracing;

namespace Whizbang.Core.Messaging;

/// <summary>
/// AOT-safe factory for creating work coordinator strategies based on configuration.
/// Uses direct <c>new</c> calls only — no reflection, no Activator.CreateInstance.
/// </summary>
/// <docs>data/work-coordinator-strategies</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/WorkCoordinatorStrategyRegistrationTests.cs:CreateStrategy_DefaultOptions_ReturnsScopedStrategyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/WorkCoordinatorStrategyRegistrationTests.cs:CreateStrategy_WithImmediateOption_ReturnsImmediateStrategyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/WorkCoordinatorStrategyRegistrationTests.cs:CreateStrategy_WithIntervalOption_ReturnsIntervalStrategyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/WorkCoordinatorStrategyRegistrationTests.cs:CreateStrategy_WithBatchOption_ReturnsBatchStrategyAsync</tests>
public static class WorkCoordinatorStrategyFactory {
  /// <summary>
  /// Creates a new work coordinator strategy instance based on the specified strategy type.
  /// Used for per-scope strategies (Scoped, Immediate) that don't require singleton lifetime.
  /// For singleton strategies (Interval, Batch), resolve them directly from the DI container.
  /// </summary>
  /// <param name="strategy">The strategy type to create.</param>
  /// <param name="sp">The service provider to resolve dependencies from.</param>
  /// <returns>A new IWorkCoordinatorStrategy instance.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/WorkCoordinatorStrategyRegistrationTests.cs:CreateStrategy_DefaultOptions_ReturnsScopedStrategyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/WorkCoordinatorStrategyRegistrationTests.cs:CreateStrategy_WithImmediateOption_ReturnsImmediateStrategyAsync</tests>
  public static IWorkCoordinatorStrategy Create(WorkCoordinatorStrategy strategy, IServiceProvider sp) {
    return strategy switch {
      WorkCoordinatorStrategy.Scoped => _createScoped(sp),
      WorkCoordinatorStrategy.Immediate => _createImmediate(sp),
      WorkCoordinatorStrategy.Interval => _createInterval(sp),
      WorkCoordinatorStrategy.Batch => _createBatch(sp),
      _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, $"Unknown work coordinator strategy: {strategy}")
    };
  }

  private static ScopedWorkCoordinatorStrategy _createScoped(IServiceProvider sp) {
    var coordinator = sp.GetRequiredService<IWorkCoordinator>();
    var instanceProvider = sp.GetRequiredService<IServiceInstanceProvider>();
    var channelWriter = sp.GetService<IWorkChannelWriter>();
    var options = sp.GetRequiredService<WorkCoordinatorOptions>();
    var logger = sp.GetService<ILogger<ScopedWorkCoordinatorStrategy>>();
    var dependencies = new ScopedWorkCoordinatorDependencies {
      ScopeFactory = sp.GetService<IServiceScopeFactory>(),
      LifecycleMessageDeserializer = sp.GetService<ILifecycleMessageDeserializer>(),
      TracingOptions = sp.GetService<IOptionsMonitor<TracingOptions>>(),
      SystemEventOptions = sp.GetService<IOptions<Whizbang.Core.SystemEvents.SystemEventOptions>>()?.Value
    };
    return new ScopedWorkCoordinatorStrategy(
      coordinator,
      instanceProvider,
      channelWriter,
      options,
      logger,
      dependencies
    );
  }

  private static ImmediateWorkCoordinatorStrategy _createImmediate(IServiceProvider sp) {
    var coordinator = sp.GetRequiredService<IWorkCoordinator>();
    var instanceProvider = sp.GetRequiredService<IServiceInstanceProvider>();
    var options = sp.GetRequiredService<WorkCoordinatorOptions>();
    var logger = sp.GetService<ILogger<ImmediateWorkCoordinatorStrategy>>();
    return new ImmediateWorkCoordinatorStrategy(
      coordinator,
      instanceProvider,
      options,
      logger,
      scopeFactory: sp.GetService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: sp.GetService<ILifecycleMessageDeserializer>(),
      tracingOptions: sp.GetService<IOptionsMonitor<TracingOptions>>(),
      deferredChannel: sp.GetService<IDeferredOutboxChannel>(),
      systemEventOptions: sp.GetService<IOptions<Whizbang.Core.SystemEvents.SystemEventOptions>>()
    );
  }

  private static IntervalWorkCoordinatorStrategy _createInterval(IServiceProvider sp) {
    var coordinator = sp.GetRequiredService<IWorkCoordinator>();
    var instanceProvider = sp.GetRequiredService<IServiceInstanceProvider>();
    var options = sp.GetRequiredService<WorkCoordinatorOptions>();
    var logger = sp.GetService<ILogger<IntervalWorkCoordinatorStrategy>>();
    return new IntervalWorkCoordinatorStrategy(
      coordinator,
      instanceProvider,
      options,
      logger,
      scopeFactory: sp.GetService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: sp.GetService<ILifecycleMessageDeserializer>(),
      tracingOptions: sp.GetService<IOptionsMonitor<TracingOptions>>()
    );
  }

  private static BatchWorkCoordinatorStrategy _createBatch(IServiceProvider sp) {
    var coordinator = sp.GetRequiredService<IWorkCoordinator>();
    var instanceProvider = sp.GetRequiredService<IServiceInstanceProvider>();
    var options = sp.GetRequiredService<WorkCoordinatorOptions>();
    var logger = sp.GetService<ILogger<BatchWorkCoordinatorStrategy>>();
    return new BatchWorkCoordinatorStrategy(
      coordinator,
      instanceProvider,
      options,
      logger,
      scopeFactory: sp.GetService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: sp.GetService<ILifecycleMessageDeserializer>(),
      tracingOptions: sp.GetService<IOptionsMonitor<TracingOptions>>()
    );
  }
}
