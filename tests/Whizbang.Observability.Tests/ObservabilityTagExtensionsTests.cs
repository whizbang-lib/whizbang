using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tags;
using Whizbang.Observability.DependencyInjection;
using Whizbang.Observability.Hooks;

namespace Whizbang.Observability.Tests;

/// <summary>
/// Locks the registration contract of
/// <see cref="ObservabilityTagExtensions"/>. Each extension method is one
/// or two lines of UseHook calls, but the assembly previously had 0% on
/// this class because no test wired a TagOptions through any of them.
/// </summary>
public class ObservabilityTagExtensionsTests {

  [Test]
  public async Task UseOpenTelemetry_ReturnsSameOptionsForChainingAsync() {
    var options = new TagOptions();
    var returned = options.UseOpenTelemetry();
    await Assert.That(returned).IsSameReferenceAs(options);
  }

  [Test]
  public async Task UseOpenTelemetry_NullOptions_ThrowsArgumentNullAsync() {
    await Assert.That(() => ObservabilityTagExtensions.UseOpenTelemetry(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task UseOpenTelemetryTracing_ReturnsSameOptionsForChainingAsync() {
    var options = new TagOptions();
    var returned = options.UseOpenTelemetryTracing();
    await Assert.That(returned).IsSameReferenceAs(options);
  }

  [Test]
  public async Task UseOpenTelemetryTracing_NullOptions_ThrowsArgumentNullAsync() {
    await Assert.That(() => ObservabilityTagExtensions.UseOpenTelemetryTracing(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task UseOpenTelemetryTracing_AcceptsCustomPriorityAsync() {
    var options = new TagOptions();
    var returned = options.UseOpenTelemetryTracing(priority: -50);
    await Assert.That(returned).IsSameReferenceAs(options);
  }

  [Test]
  public async Task UseOpenTelemetryMetrics_ReturnsSameOptionsForChainingAsync() {
    var options = new TagOptions();
    var returned = options.UseOpenTelemetryMetrics();
    await Assert.That(returned).IsSameReferenceAs(options);
  }

  [Test]
  public async Task UseOpenTelemetryMetrics_NullOptions_ThrowsArgumentNullAsync() {
    await Assert.That(() => ObservabilityTagExtensions.UseOpenTelemetryMetrics(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task UseOpenTelemetryMetrics_AcceptsCustomPriorityAsync() {
    var options = new TagOptions();
    var returned = options.UseOpenTelemetryMetrics(priority: 25);
    await Assert.That(returned).IsSameReferenceAs(options);
  }
}
