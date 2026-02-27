using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Configuration;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Configuration;

/// <summary>
/// Tests for <see cref="WhizbangCoreOptions"/>.
/// Validates default values, property setters, and TagOptions integration.
/// </summary>
/// <tests>Whizbang.Core/Configuration/WhizbangCoreOptions.cs</tests>
[Category("Core")]
[Category("Configuration")]
public class WhizbangCoreOptionsTests {

  #region Constructor Tests

  [Test]
  public async Task Constructor_InitializesTagOptions_NotNullAsync() {
    // Arrange & Act
    var options = new WhizbangCoreOptions();

    // Assert
    await Assert.That(options.Tags).IsNotNull();
  }

  [Test]
  public async Task Constructor_TagsProperty_IsNewInstance_EachTimeAsync() {
    // Arrange & Act
    var options1 = new WhizbangCoreOptions();
    var options2 = new WhizbangCoreOptions();

    // Assert - each instance should have its own TagOptions
    await Assert.That(options1.Tags).IsNotEqualTo(options2.Tags);
  }

  #endregion

  #region EnableTagProcessing Tests

  [Test]
  public async Task EnableTagProcessing_DefaultsToTrue_Async() {
    // Arrange & Act
    var options = new WhizbangCoreOptions();

    // Assert
    await Assert.That(options.EnableTagProcessing).IsTrue();
  }

  [Test]
  public async Task EnableTagProcessing_CanBeSetToFalse_Async() {
    // Arrange
    var options = new WhizbangCoreOptions();

    // Act
    options.EnableTagProcessing = false;

    // Assert
    await Assert.That(options.EnableTagProcessing).IsFalse();
  }

  [Test]
  public async Task EnableTagProcessing_CanBeSetToTrue_Async() {
    // Arrange
    var options = new WhizbangCoreOptions();
    options.EnableTagProcessing = false;

    // Act
    options.EnableTagProcessing = true;

    // Assert
    await Assert.That(options.EnableTagProcessing).IsTrue();
  }

  #endregion

  #region TagProcessingMode Tests

  [Test]
  public async Task TagProcessingMode_DefaultsToAfterReceptorCompletion_Async() {
    // Arrange & Act
    var options = new WhizbangCoreOptions();

    // Assert
    await Assert.That(options.TagProcessingMode).IsEqualTo(TagProcessingMode.AfterReceptorCompletion);
  }

  [Test]
  public async Task TagProcessingMode_CanBeSetToAsLifecycleStage_Async() {
    // Arrange
    var options = new WhizbangCoreOptions();

    // Act
    options.TagProcessingMode = TagProcessingMode.AsLifecycleStage;

    // Assert
    await Assert.That(options.TagProcessingMode).IsEqualTo(TagProcessingMode.AsLifecycleStage);
  }

  [Test]
  public async Task TagProcessingMode_CanBeSetBackToAfterReceptorCompletion_Async() {
    // Arrange
    var options = new WhizbangCoreOptions();
    options.TagProcessingMode = TagProcessingMode.AsLifecycleStage;

    // Act
    options.TagProcessingMode = TagProcessingMode.AfterReceptorCompletion;

    // Assert
    await Assert.That(options.TagProcessingMode).IsEqualTo(TagProcessingMode.AfterReceptorCompletion);
  }

  #endregion

  #region TagOptions Integration Tests

  [Test]
  public async Task Tags_UseHook_AddsRegistration_Async() {
    // Arrange
    var options = new WhizbangCoreOptions();

    // Act
    options.Tags.UseHook<NotificationTagAttribute, TestNotificationHook>();

    // Assert
    await Assert.That(options.Tags.HookRegistrations.Count).IsEqualTo(1);
    await Assert.That(options.Tags.HookRegistrations[0].AttributeType).IsEqualTo(typeof(NotificationTagAttribute));
    await Assert.That(options.Tags.HookRegistrations[0].HookType).IsEqualTo(typeof(TestNotificationHook));
  }

  [Test]
  public async Task Tags_UseHook_SupportsChainingMultipleHooks_Async() {
    // Arrange
    var options = new WhizbangCoreOptions();

    // Act
    options.Tags
      .UseHook<NotificationTagAttribute, TestNotificationHook>()
      .UseHook<TelemetryTagAttribute, TestTelemetryHook>()
      .UseHook<MetricTagAttribute, TestMetricHook>();

    // Assert
    await Assert.That(options.Tags.HookRegistrations.Count).IsEqualTo(3);
  }

  [Test]
  public async Task Tags_UseUniversalHook_WorksCorrectly_Async() {
    // Arrange
    var options = new WhizbangCoreOptions();

    // Act
    options.Tags.UseUniversalHook<TestUniversalHook>();

    // Assert
    await Assert.That(options.Tags.HookRegistrations.Count).IsEqualTo(1);
    await Assert.That(options.Tags.HookRegistrations[0].AttributeType).IsEqualTo(typeof(MessageTagAttribute));
  }

  #endregion

  #region TagProcessingMode Enum Tests

  [Test]
  public async Task TagProcessingMode_AfterReceptorCompletion_HasExpectedValue_Async() {
    // Assert - verify enum value is defined
    await Assert.That(Enum.IsDefined(TagProcessingMode.AfterReceptorCompletion)).IsTrue();
  }

  [Test]
  public async Task TagProcessingMode_AsLifecycleStage_HasExpectedValue_Async() {
    // Assert - verify enum value is defined
    await Assert.That(Enum.IsDefined(TagProcessingMode.AsLifecycleStage)).IsTrue();
  }

  [Test]
  public async Task TagProcessingMode_HasOnlyTwoValues_Async() {
    // Arrange & Act
    var values = Enum.GetValues<TagProcessingMode>();

    // Assert - only two modes should exist
    await Assert.That(values.Length).IsEqualTo(2);
  }

  #endregion

  #region Test Hook Implementations

  private sealed class TestNotificationHook : IMessageTagHook<NotificationTagAttribute> {
    public ValueTask<System.Text.Json.JsonElement?> OnTaggedMessageAsync(
        TagContext<NotificationTagAttribute> _,
        CancellationToken __) {
      return ValueTask.FromResult<System.Text.Json.JsonElement?>(null);
    }
  }

  private sealed class TestTelemetryHook : IMessageTagHook<TelemetryTagAttribute> {
    public ValueTask<System.Text.Json.JsonElement?> OnTaggedMessageAsync(
        TagContext<TelemetryTagAttribute> _,
        CancellationToken __) {
      return ValueTask.FromResult<System.Text.Json.JsonElement?>(null);
    }
  }

  private sealed class TestMetricHook : IMessageTagHook<MetricTagAttribute> {
    public ValueTask<System.Text.Json.JsonElement?> OnTaggedMessageAsync(
        TagContext<MetricTagAttribute> _,
        CancellationToken __) {
      return ValueTask.FromResult<System.Text.Json.JsonElement?>(null);
    }
  }

  private sealed class TestUniversalHook : IMessageTagHook<MessageTagAttribute> {
    public ValueTask<System.Text.Json.JsonElement?> OnTaggedMessageAsync(
        TagContext<MessageTagAttribute> _,
        CancellationToken __) {
      return ValueTask.FromResult<System.Text.Json.JsonElement?>(null);
    }
  }

  #endregion
}
