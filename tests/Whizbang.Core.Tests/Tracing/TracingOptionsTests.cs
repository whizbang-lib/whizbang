using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tracing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for TracingOptions which provides runtime configuration for tracing.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tracing/TracingOptions.cs</code-under-test>
public class TracingOptionsTests {
  #region Default Value Tests

  [Test]
  public async Task Verbosity_DefaultValue_IsOffAsync() {
    // Arrange
    var options = new TracingOptions();

    // Assert - Default verbosity is Off (production safe)
    await Assert.That(options.Verbosity).IsEqualTo(TraceVerbosity.Off);
  }

  [Test]
  public async Task Components_DefaultValue_IsNoneAsync() {
    // Arrange
    var options = new TracingOptions();

    // Assert - Default components is None
    await Assert.That(options.Components).IsEqualTo(TraceComponents.None);
  }

  [Test]
  public async Task EnableOpenTelemetry_DefaultValue_IsTrueAsync() {
    // Arrange
    var options = new TracingOptions();

    // Assert - OTel enabled by default when tracing is on
    await Assert.That(options.EnableOpenTelemetry).IsTrue();
  }

  [Test]
  public async Task EnableStructuredLogging_DefaultValue_IsTrueAsync() {
    // Arrange
    var options = new TracingOptions();

    // Assert - Structured logging enabled by default
    await Assert.That(options.EnableStructuredLogging).IsTrue();
  }

  [Test]
  public async Task TracedHandlers_DefaultValue_IsEmptyDictionaryAsync() {
    // Arrange
    var options = new TracingOptions();

    // Assert - No handlers traced by default
    await Assert.That(options.TracedHandlers).IsNotNull();
    await Assert.That(options.TracedHandlers.Count).IsEqualTo(0);
  }

  [Test]
  public async Task TracedMessages_DefaultValue_IsEmptyDictionaryAsync() {
    // Arrange
    var options = new TracingOptions();

    // Assert - No messages traced by default
    await Assert.That(options.TracedMessages).IsNotNull();
    await Assert.That(options.TracedMessages.Count).IsEqualTo(0);
  }

  #endregion

  #region Property Assignment Tests

  [Test]
  public async Task Verbosity_CanBeSetAsync() {
    // Arrange
    var options = new TracingOptions();

    // Act
    options.Verbosity = TraceVerbosity.Debug;

    // Assert
    await Assert.That(options.Verbosity).IsEqualTo(TraceVerbosity.Debug);
  }

  [Test]
  public async Task Components_CanBeSetAsync() {
    // Arrange
    var options = new TracingOptions();

    // Act
    options.Components = TraceComponents.Handlers | TraceComponents.EventStore;

    // Assert
    await Assert.That(options.Components).IsEqualTo(TraceComponents.Handlers | TraceComponents.EventStore);
  }

  [Test]
  public async Task EnableOpenTelemetry_CanBeSetToFalseAsync() {
    // Arrange
    var options = new TracingOptions();

    // Act
    options.EnableOpenTelemetry = false;

    // Assert
    await Assert.That(options.EnableOpenTelemetry).IsFalse();
  }

  [Test]
  public async Task TracedHandlers_CanBePopulatedAsync() {
    // Arrange
    var options = new TracingOptions();

    // Act
    options.TracedHandlers["OrderReceptor"] = TraceVerbosity.Debug;
    options.TracedHandlers["Payment*"] = TraceVerbosity.Verbose;

    // Assert
    await Assert.That(options.TracedHandlers.Count).IsEqualTo(2);
    await Assert.That(options.TracedHandlers["OrderReceptor"]).IsEqualTo(TraceVerbosity.Debug);
    await Assert.That(options.TracedHandlers["Payment*"]).IsEqualTo(TraceVerbosity.Verbose);
  }

  [Test]
  public async Task TracedMessages_CanBePopulatedAsync() {
    // Arrange
    var options = new TracingOptions();

    // Act
    options.TracedMessages["ReseedSystemEvent"] = TraceVerbosity.Debug;
    options.TracedMessages["Create*Command"] = TraceVerbosity.Normal;

    // Assert
    await Assert.That(options.TracedMessages.Count).IsEqualTo(2);
    await Assert.That(options.TracedMessages["ReseedSystemEvent"]).IsEqualTo(TraceVerbosity.Debug);
  }

  #endregion

  #region IsEnabled Tests

  [Test]
  public async Task IsEnabled_ReturnsFalse_WhenVerbosityIsOffAsync() {
    // Arrange
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Off,
      Components = TraceComponents.All
    };

    // Act
    var result = options.IsEnabled(TraceComponents.Handlers);

    // Assert - Off verbosity disables all tracing
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task IsEnabled_ReturnsFalse_WhenComponentNotSetAsync() {
    // Arrange
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Normal,
      Components = TraceComponents.Http
    };

    // Act
    var result = options.IsEnabled(TraceComponents.Handlers);

    // Assert - Handlers not in Components
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task IsEnabled_ReturnsTrue_WhenVerbosityAndComponentSetAsync() {
    // Arrange
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Normal,
      Components = TraceComponents.Handlers
    };

    // Act
    var result = options.IsEnabled(TraceComponents.Handlers);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task IsEnabled_ReturnsTrue_ForMultipleComponentsAsync() {
    // Arrange
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Minimal,
      Components = TraceComponents.Handlers | TraceComponents.EventStore | TraceComponents.Http
    };

    // Act & Assert - All configured components return true
    await Assert.That(options.IsEnabled(TraceComponents.Handlers)).IsTrue();
    await Assert.That(options.IsEnabled(TraceComponents.EventStore)).IsTrue();
    await Assert.That(options.IsEnabled(TraceComponents.Http)).IsTrue();
    await Assert.That(options.IsEnabled(TraceComponents.Inbox)).IsFalse();
  }

  [Test]
  public async Task IsEnabled_WithAll_ReturnsTrue_ForAnyComponentAsync() {
    // Arrange
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Normal,
      Components = TraceComponents.All
    };

    // Act & Assert
    await Assert.That(options.IsEnabled(TraceComponents.Http)).IsTrue();
    await Assert.That(options.IsEnabled(TraceComponents.Handlers)).IsTrue();
    await Assert.That(options.IsEnabled(TraceComponents.EventStore)).IsTrue();
    await Assert.That(options.IsEnabled(TraceComponents.Lifecycle)).IsTrue();
  }

  #endregion

  #region ShouldTrace Tests

  [Test]
  public async Task ShouldTrace_ReturnsFalse_WhenVerbosityIsOffAsync() {
    // Arrange
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Off
    };

    // Act
    var result = options.ShouldTrace(TraceVerbosity.Minimal);

    // Assert - Nothing traced when off
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task ShouldTrace_ReturnsTrue_WhenCurrentVerbosityMeetsRequiredAsync() {
    // Arrange
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Normal
    };

    // Act
    var result = options.ShouldTrace(TraceVerbosity.Minimal);

    // Assert - Normal >= Minimal
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task ShouldTrace_ReturnsFalse_WhenCurrentVerbosityBelowRequiredAsync() {
    // Arrange
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Minimal
    };

    // Act
    var result = options.ShouldTrace(TraceVerbosity.Verbose);

    // Assert - Minimal < Verbose
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task ShouldTrace_ReturnsTrue_WhenDebugAndDebugRequiredAsync() {
    // Arrange
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Debug
    };

    // Act
    var result = options.ShouldTrace(TraceVerbosity.Debug);

    // Assert
    await Assert.That(result).IsTrue();
  }

  #endregion

  #region Class Definition Tests

  [Test]
  public async Task TracingOptions_IsSealedAsync() {
    // Arrange
    var type = typeof(TracingOptions);

    // Assert - Options should be sealed
    await Assert.That(type.IsSealed).IsTrue();
  }

  [Test]
  public async Task TracingOptions_HasParameterlessConstructorAsync() {
    // Arrange & Act - Configuration binding requires parameterless constructor
    var options = new TracingOptions();

    // Assert
    await Assert.That(options).IsNotNull();
  }

  #endregion

  #region Dictionary Independence Tests

  [Test]
  public async Task NewInstance_HasIndependentDictionariesAsync() {
    // Arrange
    var options1 = new TracingOptions();
    var options2 = new TracingOptions();

    // Act
    options1.TracedHandlers["Test"] = TraceVerbosity.Debug;

    // Assert - Dictionaries are independent
    await Assert.That(options1.TracedHandlers.ContainsKey("Test")).IsTrue();
    await Assert.That(options2.TracedHandlers.ContainsKey("Test")).IsFalse();
  }

  #endregion
}
