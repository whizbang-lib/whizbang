using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tracing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for MetricsOptions which provides runtime configuration for the metrics system.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tracing/MetricsOptions.cs</code-under-test>
public class MetricsOptionsTests {
  #region Default Values

  [Test]
  public async Task Enabled_Default_IsFalseAsync() {
    // Arrange
    var options = new MetricsOptions();

    // Assert - Disabled by default for production safety
    await Assert.That(options.Enabled).IsFalse();
  }

  [Test]
  public async Task Components_Default_IsNoneAsync() {
    // Arrange
    var options = new MetricsOptions();

    // Assert
    await Assert.That(options.Components).IsEqualTo(MetricComponents.None);
  }

  [Test]
  public async Task MeterName_Default_IsWhizbangAsync() {
    // Arrange
    var options = new MetricsOptions();

    // Assert
    await Assert.That(options.MeterName).IsEqualTo("Whizbang");
  }

  [Test]
  public async Task MeterVersion_Default_IsNullAsync() {
    // Arrange
    var options = new MetricsOptions();

    // Assert - null means use assembly version
    await Assert.That(options.MeterVersion).IsNull();
  }

  [Test]
  public async Task IncludeHandlerNameTag_Default_IsTrueAsync() {
    // Arrange
    var options = new MetricsOptions();

    // Assert
    await Assert.That(options.IncludeHandlerNameTag).IsTrue();
  }

  [Test]
  public async Task IncludeMessageTypeTag_Default_IsTrueAsync() {
    // Arrange
    var options = new MetricsOptions();

    // Assert
    await Assert.That(options.IncludeMessageTypeTag).IsTrue();
  }

  [Test]
  public async Task DurationBuckets_Default_HasTwelveValuesAsync() {
    // Arrange
    var options = new MetricsOptions();

    // Assert
    await Assert.That(options.DurationBuckets.Length).IsEqualTo(12);
  }

  [Test]
  public async Task DurationBuckets_Default_HasExpectedValuesAsync() {
    // Arrange
    var options = new MetricsOptions();
    var expected = new double[] { 1, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000 };

    // Assert
    await Assert.That(options.DurationBuckets).IsEquivalentTo(expected);
  }

  #endregion

  #region IsEnabled Tests

  [Test]
  public async Task IsEnabled_WhenDisabled_ReturnsFalseForAllComponentsAsync() {
    // Arrange
    var options = new MetricsOptions {
      Enabled = false,
      Components = MetricComponents.All
    };

    // Assert - Even with All components, disabled means no metrics
    await Assert.That(options.IsEnabled(MetricComponents.Handlers)).IsFalse();
    await Assert.That(options.IsEnabled(MetricComponents.EventStore)).IsFalse();
    await Assert.That(options.IsEnabled(MetricComponents.Errors)).IsFalse();
  }

  [Test]
  public async Task IsEnabled_WhenEnabledWithNone_ReturnsFalseForAllComponentsAsync() {
    // Arrange
    var options = new MetricsOptions {
      Enabled = true,
      Components = MetricComponents.None
    };

    // Assert - Enabled but no components selected
    await Assert.That(options.IsEnabled(MetricComponents.Handlers)).IsFalse();
    await Assert.That(options.IsEnabled(MetricComponents.EventStore)).IsFalse();
    await Assert.That(options.IsEnabled(MetricComponents.Errors)).IsFalse();
  }

  [Test]
  public async Task IsEnabled_WhenEnabledWithAll_ReturnsTrueForAllComponentsAsync() {
    // Arrange
    var options = new MetricsOptions {
      Enabled = true,
      Components = MetricComponents.All
    };

    // Assert
    await Assert.That(options.IsEnabled(MetricComponents.Handlers)).IsTrue();
    await Assert.That(options.IsEnabled(MetricComponents.EventStore)).IsTrue();
    await Assert.That(options.IsEnabled(MetricComponents.Errors)).IsTrue();
    await Assert.That(options.IsEnabled(MetricComponents.Policies)).IsTrue();
  }

  [Test]
  public async Task IsEnabled_WhenEnabledWithSpecificComponents_ReturnsTrueOnlyForThoseAsync() {
    // Arrange
    var options = new MetricsOptions {
      Enabled = true,
      Components = MetricComponents.Handlers | MetricComponents.EventStore
    };

    // Assert
    await Assert.That(options.IsEnabled(MetricComponents.Handlers)).IsTrue();
    await Assert.That(options.IsEnabled(MetricComponents.EventStore)).IsTrue();
    await Assert.That(options.IsEnabled(MetricComponents.Errors)).IsFalse();
    await Assert.That(options.IsEnabled(MetricComponents.Dispatcher)).IsFalse();
  }

  [Test]
  public async Task IsEnabled_ChecksExactComponent_NotCompositeAsync() {
    // Arrange
    var options = new MetricsOptions {
      Enabled = true,
      Components = MetricComponents.Handlers
    };

    // Assert - Only Handlers should be enabled
    await Assert.That(options.IsEnabled(MetricComponents.Handlers)).IsTrue();
    await Assert.That(options.IsEnabled(MetricComponents.None)).IsTrue(); // None is always true when enabled
    await Assert.That(options.IsEnabled(MetricComponents.All)).IsFalse(); // All is not a subset of Handlers
  }

  #endregion

  #region Property Setters

  [Test]
  public async Task Enabled_CanBeSetToTrueAsync() {
    // Arrange
    var options = new MetricsOptions();

    // Act
    options.Enabled = true;

    // Assert
    await Assert.That(options.Enabled).IsTrue();
  }

  [Test]
  public async Task Components_CanBeSetAsync() {
    // Arrange
    var options = new MetricsOptions();

    // Act
    options.Components = MetricComponents.Handlers | MetricComponents.Errors;

    // Assert
    await Assert.That(options.Components).IsEqualTo(MetricComponents.Handlers | MetricComponents.Errors);
  }

  [Test]
  public async Task MeterName_CanBeSetAsync() {
    // Arrange
    var options = new MetricsOptions();

    // Act
    options.MeterName = "MyApp.Metrics";

    // Assert
    await Assert.That(options.MeterName).IsEqualTo("MyApp.Metrics");
  }

  [Test]
  public async Task MeterVersion_CanBeSetAsync() {
    // Arrange
    var options = new MetricsOptions();

    // Act
    options.MeterVersion = "2.0.0";

    // Assert
    await Assert.That(options.MeterVersion).IsEqualTo("2.0.0");
  }

  [Test]
  public async Task IncludeHandlerNameTag_CanBeSetToFalseAsync() {
    // Arrange
    var options = new MetricsOptions();

    // Act
    options.IncludeHandlerNameTag = false;

    // Assert
    await Assert.That(options.IncludeHandlerNameTag).IsFalse();
  }

  [Test]
  public async Task IncludeMessageTypeTag_CanBeSetToFalseAsync() {
    // Arrange
    var options = new MetricsOptions();

    // Act
    options.IncludeMessageTypeTag = false;

    // Assert
    await Assert.That(options.IncludeMessageTypeTag).IsFalse();
  }

  [Test]
  public async Task DurationBuckets_CanBeSetAsync() {
    // Arrange
    var options = new MetricsOptions();
    var customBuckets = new double[] { 1, 10, 100, 1000 };

    // Act
    options.DurationBuckets = customBuckets;

    // Assert
    await Assert.That(options.DurationBuckets).IsEquivalentTo(customBuckets);
  }

  #endregion

  #region Production Configuration Scenarios

  [Test]
  public async Task ProductionDefaults_AllDisabledAsync() {
    // Arrange - Production should have minimal overhead
    var options = new MetricsOptions();

    // Assert - Everything disabled by default
    await Assert.That(options.Enabled).IsFalse();
    await Assert.That(options.Components).IsEqualTo(MetricComponents.None);
    await Assert.That(options.IsEnabled(MetricComponents.Handlers)).IsFalse();
  }

  [Test]
  public async Task DevelopmentConfiguration_FullVisibilityAsync() {
    // Arrange - Development wants to see everything
    var options = new MetricsOptions {
      Enabled = true,
      Components = MetricComponents.All
    };

    // Assert
    await Assert.That(options.IsEnabled(MetricComponents.Handlers)).IsTrue();
    await Assert.That(options.IsEnabled(MetricComponents.EventStore)).IsTrue();
    await Assert.That(options.IsEnabled(MetricComponents.Lifecycle)).IsTrue();
    await Assert.That(options.IsEnabled(MetricComponents.Perspectives)).IsTrue();
  }

  [Test]
  public async Task HighCardinalityMitigation_DisableTagsAsync() {
    // Arrange - Large systems may need to disable handler/message tags
    var options = new MetricsOptions {
      Enabled = true,
      Components = MetricComponents.Handlers,
      IncludeHandlerNameTag = false,
      IncludeMessageTypeTag = false
    };

    // Assert
    await Assert.That(options.IncludeHandlerNameTag).IsFalse();
    await Assert.That(options.IncludeMessageTypeTag).IsFalse();
    await Assert.That(options.IsEnabled(MetricComponents.Handlers)).IsTrue();
  }

  #endregion
}
