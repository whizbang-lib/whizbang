using System;
using System.Linq;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for <see cref="TagOptions"/>.
/// Validates hook registration, priority ordering, and fluent API.
/// </summary>
/// <tests>Whizbang.Core/Tags/TagOptions.cs</tests>
[Category("Core")]
[Category("Tags")]
public class TagOptionsTests {

  [Test]
  public async Task TagOptions_HookRegistrations_IsEmptyByDefaultAsync() {
    // Arrange & Act
    var options = new TagOptions();

    // Assert
    await Assert.That(options.HookRegistrations).IsEmpty();
  }

  [Test]
  public async Task UseHook_AddsRegistrationToListAsync() {
    // Arrange
    var options = new TagOptions();

    // Act
    options.UseHook<NotificationTagAttribute, TestNotificationHook>();

    // Assert
    await Assert.That(options.HookRegistrations.Count).IsEqualTo(1);
    await Assert.That(options.HookRegistrations[0].AttributeType).IsEqualTo(typeof(NotificationTagAttribute));
    await Assert.That(options.HookRegistrations[0].HookType).IsEqualTo(typeof(TestNotificationHook));
  }

  [Test]
  public async Task UseHook_UsesDefaultPriorityAsync() {
    // Arrange
    var options = new TagOptions();

    // Act
    options.UseHook<NotificationTagAttribute, TestNotificationHook>();

    // Assert
    await Assert.That(options.HookRegistrations[0].Priority).IsEqualTo(-100);
  }

  [Test]
  public async Task UseHook_AcceptsCustomPriorityAsync() {
    // Arrange
    var options = new TagOptions();

    // Act
    options.UseHook<NotificationTagAttribute, TestNotificationHook>(priority: 50);

    // Assert
    await Assert.That(options.HookRegistrations[0].Priority).IsEqualTo(50);
  }

  [Test]
  public async Task UseHook_ReturnsSameInstanceForChainingAsync() {
    // Arrange
    var options = new TagOptions();

    // Act
    var result = options.UseHook<NotificationTagAttribute, TestNotificationHook>();

    // Assert
    await Assert.That(result).IsEqualTo(options);
  }

  [Test]
  public async Task UseHook_AllowsMultipleHooksForSameAttributeTypeAsync() {
    // Arrange
    var options = new TagOptions();

    // Act
    options
      .UseHook<NotificationTagAttribute, TestNotificationHook>()
      .UseHook<NotificationTagAttribute, TestNotificationHook2>();

    // Assert
    await Assert.That(options.HookRegistrations.Count).IsEqualTo(2);
  }

  [Test]
  public async Task UseHook_AllowsMultipleAttributeTypesAsync() {
    // Arrange
    var options = new TagOptions();

    // Act
    options
      .UseHook<NotificationTagAttribute, TestNotificationHook>()
      .UseHook<TelemetryTagAttribute, TestTelemetryHook>()
      .UseHook<MetricTagAttribute, TestMetricHook>();

    // Assert
    await Assert.That(options.HookRegistrations.Count).IsEqualTo(3);
  }

  [Test]
  public async Task UseUniversalHook_RegistersForMessageTagAttributeAsync() {
    // Arrange
    var options = new TagOptions();

    // Act
    options.UseUniversalHook<TestUniversalHook>();

    // Assert
    await Assert.That(options.HookRegistrations[0].AttributeType).IsEqualTo(typeof(MessageTagAttribute));
  }

  [Test]
  public async Task GetHooksInExecutionOrder_SortsByPriorityAscendingAsync() {
    // Arrange
    var options = new TagOptions();
    options
      .UseHook<NotificationTagAttribute, TestNotificationHook>(priority: 500)
      .UseHook<TelemetryTagAttribute, TestTelemetryHook>(priority: -100)
      .UseHook<MetricTagAttribute, TestMetricHook>(priority: 30)
      .UseHook<NotificationTagAttribute, TestNotificationHook2>(priority: -10);

    // Act
    var sorted = options.GetHooksInExecutionOrder().ToArray();

    // Assert
    await Assert.That(sorted[0].Priority).IsEqualTo(-100);
    await Assert.That(sorted[1].Priority).IsEqualTo(-10);
    await Assert.That(sorted[2].Priority).IsEqualTo(30);
    await Assert.That(sorted[3].Priority).IsEqualTo(500);
  }

  [Test]
  public async Task GetHooksFor_Generic_ReturnsOnlyMatchingTypeAsync() {
    // Arrange
    var options = new TagOptions();
    options
      .UseHook<NotificationTagAttribute, TestNotificationHook>()
      .UseHook<TelemetryTagAttribute, TestTelemetryHook>()
      .UseHook<NotificationTagAttribute, TestNotificationHook2>();

    // Act
    var hooks = options.GetHooksFor<NotificationTagAttribute>().ToArray();

    // Assert
    await Assert.That(hooks.Length).IsEqualTo(2);
    await Assert.That(hooks.All(h => h.AttributeType == typeof(NotificationTagAttribute))).IsTrue();
  }

  [Test]
  public async Task GetHooksFor_IncludesUniversalHooksAsync() {
    // Arrange
    var options = new TagOptions();
    options
      .UseUniversalHook<TestUniversalHook>()
      .UseHook<NotificationTagAttribute, TestNotificationHook>();

    // Act
    var hooks = options.GetHooksFor<NotificationTagAttribute>().ToArray();

    // Assert
    await Assert.That(hooks.Length).IsEqualTo(2);
  }

  [Test]
  public async Task GetHooksFor_NonGeneric_ReturnsOnlyMatchingTypeAsync() {
    // Arrange
    var options = new TagOptions();
    options
      .UseHook<NotificationTagAttribute, TestNotificationHook>()
      .UseHook<TelemetryTagAttribute, TestTelemetryHook>();

    // Act - Using non-generic intentionally to test that overload
#pragma warning disable CA2263
    var hooks = options.GetHooksFor(typeof(TelemetryTagAttribute)).ToArray();
#pragma warning restore CA2263

    // Assert
    await Assert.That(hooks.Length).IsEqualTo(1);
    await Assert.That(hooks[0].AttributeType).IsEqualTo(typeof(TelemetryTagAttribute));
  }

  [Test]
  public async Task GetHooksFor_NonGeneric_ThrowsOnNullTypeAsync() {
    // Arrange
    var options = new TagOptions();

    // Act & Assert - Using non-generic intentionally to test that overload
    await Assert.ThrowsAsync<ArgumentNullException>(async () => {
#pragma warning disable CA2263
      _ = options.GetHooksFor(null!).ToArray();
#pragma warning restore CA2263
      await Task.CompletedTask;
    });
  }

  [Test]
  public async Task GetHooksFor_SortsByPriorityAsync() {
    // Arrange
    var options = new TagOptions();
    options
      .UseHook<NotificationTagAttribute, TestNotificationHook>(priority: 100)
      .UseHook<NotificationTagAttribute, TestNotificationHook2>(priority: -50);

    // Act
    var hooks = options.GetHooksFor<NotificationTagAttribute>().ToArray();

    // Assert
    await Assert.That(hooks[0].Priority).IsEqualTo(-50);
    await Assert.That(hooks[1].Priority).IsEqualTo(100);
  }

  // Test hook implementations
  private sealed class TestNotificationHook : IMessageTagHook<NotificationTagAttribute> {
    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<NotificationTagAttribute> _,
        CancellationToken __) {
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class TestNotificationHook2 : IMessageTagHook<NotificationTagAttribute> {
    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<NotificationTagAttribute> _,
        CancellationToken __) {
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class TestTelemetryHook : IMessageTagHook<TelemetryTagAttribute> {
    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<TelemetryTagAttribute> _,
        CancellationToken __) {
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class TestMetricHook : IMessageTagHook<MetricTagAttribute> {
    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<MetricTagAttribute> _,
        CancellationToken __) {
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class TestUniversalHook : IMessageTagHook<MessageTagAttribute> {
    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<MessageTagAttribute> _,
        CancellationToken __) {
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }
}
