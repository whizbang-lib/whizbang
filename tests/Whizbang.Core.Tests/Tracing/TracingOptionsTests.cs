using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tracing;

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for <see cref="TracingOptions"/> configuration class.
/// </summary>
public class TracingOptionsTests {
  // ==========================================================================
  // Default Value Tests
  // ==========================================================================

  [Test]
  public async Task Verbosity_DefaultValue_IsOffAsync() {
    var options = new TracingOptions();

    await Assert.That(options.Verbosity).IsEqualTo(TraceVerbosity.Off);
  }

  [Test]
  public async Task Components_DefaultValue_IsNoneAsync() {
    var options = new TracingOptions();

    await Assert.That(options.Components).IsEqualTo(TraceComponents.None);
  }

  [Test]
  public async Task EnableOpenTelemetry_DefaultValue_IsTrueAsync() {
    var options = new TracingOptions();

    await Assert.That(options.EnableOpenTelemetry).IsTrue();
  }

  [Test]
  public async Task EnableStructuredLogging_DefaultValue_IsTrueAsync() {
    var options = new TracingOptions();

    await Assert.That(options.EnableStructuredLogging).IsTrue();
  }

  [Test]
  public async Task TracedHandlers_DefaultValue_IsEmptyDictionaryAsync() {
    var options = new TracingOptions();

    await Assert.That(options.TracedHandlers).IsNotNull();
    await Assert.That(options.TracedHandlers.Count).IsEqualTo(0);
  }

  [Test]
  public async Task TracedMessages_DefaultValue_IsEmptyDictionaryAsync() {
    var options = new TracingOptions();

    await Assert.That(options.TracedMessages).IsNotNull();
    await Assert.That(options.TracedMessages.Count).IsEqualTo(0);
  }

  // ==========================================================================
  // Property Setter Tests
  // ==========================================================================

  [Test]
  public async Task Verbosity_CanBeSetAsync() {
    var options = new TracingOptions { Verbosity = TraceVerbosity.Debug };

    await Assert.That(options.Verbosity).IsEqualTo(TraceVerbosity.Debug);
  }

  [Test]
  public async Task Components_CanBeSetAsync() {
    var options = new TracingOptions {
      Components = TraceComponents.Handlers | TraceComponents.Lifecycle
    };

    await Assert.That(options.Components.HasFlag(TraceComponents.Handlers)).IsTrue();
    await Assert.That(options.Components.HasFlag(TraceComponents.Lifecycle)).IsTrue();
  }

  [Test]
  public async Task EnableOpenTelemetry_CanBeSetToFalseAsync() {
    var options = new TracingOptions { EnableOpenTelemetry = false };

    await Assert.That(options.EnableOpenTelemetry).IsFalse();
  }

  [Test]
  public async Task EnableStructuredLogging_CanBeSetToFalseAsync() {
    var options = new TracingOptions { EnableStructuredLogging = false };

    await Assert.That(options.EnableStructuredLogging).IsFalse();
  }

  [Test]
  public async Task TracedHandlers_CanBePopulatedAsync() {
    var options = new TracingOptions();
    options.TracedHandlers["OrderReceptor"] = TraceVerbosity.Debug;
    options.TracedHandlers["Payment*"] = TraceVerbosity.Verbose;

    await Assert.That(options.TracedHandlers.Count).IsEqualTo(2);
    await Assert.That(options.TracedHandlers["OrderReceptor"]).IsEqualTo(TraceVerbosity.Debug);
    await Assert.That(options.TracedHandlers["Payment*"]).IsEqualTo(TraceVerbosity.Verbose);
  }

  [Test]
  public async Task TracedMessages_CanBePopulatedAsync() {
    var options = new TracingOptions();
    options.TracedMessages["CreateOrderCommand"] = TraceVerbosity.Debug;
    options.TracedMessages["*Event"] = TraceVerbosity.Normal;

    await Assert.That(options.TracedMessages.Count).IsEqualTo(2);
    await Assert.That(options.TracedMessages["CreateOrderCommand"]).IsEqualTo(TraceVerbosity.Debug);
    await Assert.That(options.TracedMessages["*Event"]).IsEqualTo(TraceVerbosity.Normal);
  }

  // ==========================================================================
  // IsEnabled Tests
  // ==========================================================================

  [Test]
  public async Task IsEnabled_ReturnsFalse_WhenVerbosityIsOffAsync() {
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Off,
      Components = TraceComponents.All
    };

    await Assert.That(options.IsEnabled(TraceComponents.Handlers)).IsFalse();
  }

  [Test]
  public async Task IsEnabled_ReturnsFalse_WhenComponentNotSetAsync() {
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Verbose,
      Components = TraceComponents.Handlers // Only Handlers
    };

    await Assert.That(options.IsEnabled(TraceComponents.Lifecycle)).IsFalse();
  }

  [Test]
  public async Task IsEnabled_ReturnsTrue_WhenVerbosityAndComponentSetAsync() {
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Normal,
      Components = TraceComponents.Handlers
    };

    await Assert.That(options.IsEnabled(TraceComponents.Handlers)).IsTrue();
  }

  [Test]
  public async Task IsEnabled_ReturnsTrue_ForMultipleComponentsAsync() {
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Verbose,
      Components = TraceComponents.Handlers | TraceComponents.Lifecycle | TraceComponents.Errors
    };

    await Assert.That(options.IsEnabled(TraceComponents.Handlers)).IsTrue();
    await Assert.That(options.IsEnabled(TraceComponents.Lifecycle)).IsTrue();
    await Assert.That(options.IsEnabled(TraceComponents.Errors)).IsTrue();
    await Assert.That(options.IsEnabled(TraceComponents.Outbox)).IsFalse();
  }

  [Test]
  public async Task IsEnabled_WithAll_ReturnsTrue_ForAnyComponentAsync() {
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Minimal,
      Components = TraceComponents.All
    };

    await Assert.That(options.IsEnabled(TraceComponents.Handlers)).IsTrue();
    await Assert.That(options.IsEnabled(TraceComponents.Outbox)).IsTrue();
    await Assert.That(options.IsEnabled(TraceComponents.Perspectives)).IsTrue();
  }

  // ==========================================================================
  // ShouldTrace Tests (Verbosity Level Check)
  // ==========================================================================

  [Test]
  public async Task ShouldTrace_ReturnsFalse_WhenVerbosityIsOffAsync() {
    var options = new TracingOptions { Verbosity = TraceVerbosity.Off };

    await Assert.That(options.ShouldTrace(TraceVerbosity.Minimal)).IsFalse();
  }

  [Test]
  public async Task ShouldTrace_ReturnsTrue_WhenCurrentVerbosityMeetsRequiredAsync() {
    var options = new TracingOptions { Verbosity = TraceVerbosity.Normal };

    await Assert.That(options.ShouldTrace(TraceVerbosity.Minimal)).IsTrue();
    await Assert.That(options.ShouldTrace(TraceVerbosity.Normal)).IsTrue();
  }

  [Test]
  public async Task ShouldTrace_ReturnsFalse_WhenCurrentVerbosityBelowRequiredAsync() {
    var options = new TracingOptions { Verbosity = TraceVerbosity.Normal };

    await Assert.That(options.ShouldTrace(TraceVerbosity.Verbose)).IsFalse();
    await Assert.That(options.ShouldTrace(TraceVerbosity.Debug)).IsFalse();
  }

  [Test]
  public async Task ShouldTrace_ReturnsTrue_WhenDebugAndDebugRequiredAsync() {
    var options = new TracingOptions { Verbosity = TraceVerbosity.Debug };

    await Assert.That(options.ShouldTrace(TraceVerbosity.Debug)).IsTrue();
  }

  // ==========================================================================
  // Type Tests
  // ==========================================================================

  [Test]
  public async Task TracingOptions_IsSealedAsync() {
    await Assert.That(typeof(TracingOptions).IsSealed).IsTrue();
  }

  [Test]
  public async Task TracingOptions_HasParameterlessConstructorAsync() {
    var constructor = typeof(TracingOptions).GetConstructor(Type.EmptyTypes);

    await Assert.That(constructor).IsNotNull();
  }

  [Test]
  public async Task NewInstance_HasIndependentDictionariesAsync() {
    var options1 = new TracingOptions();
    var options2 = new TracingOptions();

    options1.TracedHandlers["Handler1"] = TraceVerbosity.Debug;

    await Assert.That(options2.TracedHandlers.ContainsKey("Handler1")).IsFalse();
  }
}
