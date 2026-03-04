using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Testing.Observability;

namespace Whizbang.Observability.Tests;

/// <summary>
/// Tests that validate trace output against baseline snapshots.
/// Demonstrates the snapshot testing approach for locking in trace structure.
/// </summary>
/// <remarks>
/// <para>
/// These tests create realistic trace hierarchies simulating Whizbang command dispatch
/// and verify they match expected baseline snapshots.
/// </para>
/// <para>
/// To regenerate baselines, set <c>REGENERATE_BASELINES=true</c> environment variable
/// or call <see cref="InMemorySpanCollector.SaveBaselineAsync"/> directly.
/// </para>
/// </remarks>
[NotInParallel(Order = 3)]
public class TraceBaselineTests {
  private static readonly string _baselinesPath = Path.Combine(
    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!,
    "..", "..", "..", "Baselines");

  [Test]
  public async Task CommandDispatch_MatchesBaselineAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var baselinePath = Path.Combine(_baselinesPath, "command-dispatch.json");

    // Act - Simulate a typical command dispatch trace
    _simulateCommandDispatch(collector);

    // Assert or regenerate baseline
    if (Environment.GetEnvironmentVariable("REGENERATE_BASELINES") == "true") {
      await collector.SaveBaselineAsync(baselinePath);
      await Assert.That(File.Exists(baselinePath)).IsTrue();
    } else if (File.Exists(baselinePath)) {
      await collector.AssertMatchesBaselineFileAsync(baselinePath);
    } else {
      // First run - generate baseline
      await collector.SaveBaselineAsync(baselinePath);
      await Assert.That(File.Exists(baselinePath)).IsTrue();
    }
  }

  [Test]
  public async Task LifecycleStages_MatchesBaselineAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var baselinePath = Path.Combine(_baselinesPath, "lifecycle-stages.json");

    // Act - Simulate lifecycle stages
    _simulateLifecycleStages(collector);

    // Assert or regenerate baseline
    if (Environment.GetEnvironmentVariable("REGENERATE_BASELINES") == "true") {
      await collector.SaveBaselineAsync(baselinePath);
      await Assert.That(File.Exists(baselinePath)).IsTrue();
    } else if (File.Exists(baselinePath)) {
      await collector.AssertMatchesBaselineFileAsync(baselinePath);
    } else {
      // First run - generate baseline
      await collector.SaveBaselineAsync(baselinePath);
      await Assert.That(File.Exists(baselinePath)).IsTrue();
    }
  }

  [Test]
  public async Task MultipleHandlers_MatchesBaselineAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var baselinePath = Path.Combine(_baselinesPath, "multiple-handlers.json");

    // Act - Simulate multiple handlers for an event
    _simulateMultipleHandlers(collector);

    // Assert or regenerate baseline
    if (Environment.GetEnvironmentVariable("REGENERATE_BASELINES") == "true") {
      await collector.SaveBaselineAsync(baselinePath);
      await Assert.That(File.Exists(baselinePath)).IsTrue();
    } else if (File.Exists(baselinePath)) {
      await collector.AssertMatchesBaselineFileAsync(baselinePath);
    } else {
      // First run - generate baseline
      await collector.SaveBaselineAsync(baselinePath);
      await Assert.That(File.Exists(baselinePath)).IsTrue();
    }
  }

  [Test]
  public async Task TraceWithError_MatchesBaselineAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var baselinePath = Path.Combine(_baselinesPath, "trace-with-error.json");

    // Act - Simulate a trace with error
    _simulateTraceWithError(collector);

    // Assert or regenerate baseline
    if (Environment.GetEnvironmentVariable("REGENERATE_BASELINES") == "true") {
      await collector.SaveBaselineAsync(baselinePath);
      await Assert.That(File.Exists(baselinePath)).IsTrue();
    } else if (File.Exists(baselinePath)) {
      await collector.AssertMatchesBaselineFileAsync(baselinePath);
    } else {
      // First run - generate baseline
      await collector.SaveBaselineAsync(baselinePath);
      await Assert.That(File.Exists(baselinePath)).IsTrue();
    }
  }

  /// <summary>
  /// Simulates a typical command dispatch trace hierarchy:
  /// - Dispatch CreateOrderCommand
  ///   - Handler: OrderReceptor
  /// </summary>
  private static void _simulateCommandDispatch(InMemorySpanCollector collector) {
    using var dispatch = WhizbangActivitySource.Tracing.StartActivity("Dispatch CreateOrderCommand");
    dispatch?.SetTag("whizbang.message.type", "CreateOrderCommand");
    dispatch?.SetTag("whizbang.route", "Direct");

    using var handler = WhizbangActivitySource.Tracing.StartActivity("Handler: OrderReceptor");
    handler?.SetTag("whizbang.handler.name", "ECommerce.Orders.OrderReceptor");
    handler?.SetTag("whizbang.handler.status", "Success");
    handler?.SetStatus(ActivityStatusCode.Ok);
  }

  /// <summary>
  /// Simulates lifecycle stages trace hierarchy:
  /// - Dispatch ReseedSystemCommand
  ///   - Lifecycle PreDistributeInline
  ///   - Lifecycle PreDistributeAsync
  ///   - Lifecycle DistributeAsync
  ///   - Lifecycle PostDistributeInline
  ///   - Lifecycle PostDistributeAsync
  /// </summary>
  private static void _simulateLifecycleStages(InMemorySpanCollector collector) {
    using var dispatch = WhizbangActivitySource.Tracing.StartActivity("Dispatch ReseedSystemCommand");
    dispatch?.SetTag("whizbang.message.type", "ReseedSystemCommand");

    using (var stage1 = WhizbangActivitySource.Tracing.StartActivity("Lifecycle PreDistributeInline")) {
      stage1?.SetTag("whizbang.lifecycle.stage", "PreDistributeInline");
    }

    using (var stage2 = WhizbangActivitySource.Tracing.StartActivity("Lifecycle PreDistributeAsync")) {
      stage2?.SetTag("whizbang.lifecycle.stage", "PreDistributeAsync");
    }

    using (var stage3 = WhizbangActivitySource.Tracing.StartActivity("Lifecycle DistributeAsync")) {
      stage3?.SetTag("whizbang.lifecycle.stage", "DistributeAsync");
    }

    using (var stage4 = WhizbangActivitySource.Tracing.StartActivity("Lifecycle PostDistributeInline")) {
      stage4?.SetTag("whizbang.lifecycle.stage", "PostDistributeInline");
    }

    using (var stage5 = WhizbangActivitySource.Tracing.StartActivity("Lifecycle PostDistributeAsync")) {
      stage5?.SetTag("whizbang.lifecycle.stage", "PostDistributeAsync");
    }
  }

  /// <summary>
  /// Simulates multiple handlers for an event:
  /// - Dispatch OrderCreatedEvent
  ///   - Handler: NotificationHandler
  ///   - Handler: InventoryHandler
  ///   - Handler: AnalyticsHandler (explicit)
  /// </summary>
  private static void _simulateMultipleHandlers(InMemorySpanCollector collector) {
    using var dispatch = WhizbangActivitySource.Tracing.StartActivity("Dispatch OrderCreatedEvent");
    dispatch?.SetTag("whizbang.message.type", "OrderCreatedEvent");
    dispatch?.SetTag("whizbang.handler.count", 3);

    using (var h1 = WhizbangActivitySource.Tracing.StartActivity("Handler: NotificationHandler")) {
      h1?.SetTag("whizbang.handler.name", "NotificationHandler");
      h1?.SetTag("whizbang.trace.explicit", false);
      h1?.SetStatus(ActivityStatusCode.Ok);
    }

    using (var h2 = WhizbangActivitySource.Tracing.StartActivity("Handler: InventoryHandler")) {
      h2?.SetTag("whizbang.handler.name", "InventoryHandler");
      h2?.SetTag("whizbang.trace.explicit", false);
      h2?.SetStatus(ActivityStatusCode.Ok);
    }

    using (var h3 = WhizbangActivitySource.Tracing.StartActivity("Handler: AnalyticsHandler")) {
      h3?.SetTag("whizbang.handler.name", "AnalyticsHandler");
      h3?.SetTag("whizbang.trace.explicit", true);
      h3?.SetStatus(ActivityStatusCode.Ok);
    }
  }

  /// <summary>
  /// Simulates a trace with an error:
  /// - Dispatch PaymentCommand
  ///   - Handler: PaymentHandler (failed)
  /// </summary>
  private static void _simulateTraceWithError(InMemorySpanCollector collector) {
    using var dispatch = WhizbangActivitySource.Tracing.StartActivity("Dispatch PaymentCommand");
    dispatch?.SetTag("whizbang.message.type", "PaymentCommand");

    using var handler = WhizbangActivitySource.Tracing.StartActivity("Handler: PaymentHandler");
    handler?.SetTag("whizbang.handler.name", "PaymentHandler");
    handler?.SetTag("whizbang.handler.status", "Failed");
    handler?.SetStatus(ActivityStatusCode.Error, "Payment gateway timeout");

    // Record exception event
    var exceptionTags = new ActivityTagsCollection {
      { "exception.type", "System.TimeoutException" },
      { "exception.message", "Payment gateway timeout" }
    };
    handler?.AddEvent(new ActivityEvent("exception", tags: exceptionTags));
  }
}
