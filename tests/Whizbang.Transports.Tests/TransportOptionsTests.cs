using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.Tests;

/// <summary>
/// Tests for TransportOptions abstract base class.
/// Locks in: default values, capability-gated validation warnings, and inheritance behavior.
/// Following TDD: These tests are written BEFORE the implementation.
/// </summary>
public class TransportOptionsTests {
  // ========================================
  // CONCRETE SUBCLASS FOR TESTING
  // ========================================

  /// <summary>
  /// Minimal concrete subclass that allows testing the abstract TransportOptions base.
  /// Adds a transport-specific nullable override property to test precedence.
  /// </summary>
  private sealed class TestTransportOptions : TransportOptions {
  }

  /// <summary>
  /// Subclass that overrides base defaults (simulates RabbitMQ-like behavior
  /// where EnableOrderedDelivery defaults to false instead of true).
  /// </summary>
  private sealed class OverriddenDefaultsOptions : TransportOptions {
    public OverriddenDefaultsOptions() {
      EnableOrderedDelivery = false;
      MessagePrefetchCount = 10;
    }
  }

  // ========================================
  // DEFAULT VALUES — Message Processing
  // ========================================

  [Test]
  public async Task ConcurrentMessageLimit_DefaultsTo10Async() {
    var options = new TestTransportOptions();
    await Assert.That(options.ConcurrentMessageLimit).IsEqualTo(10);
  }

  [Test]
  public async Task MessagePrefetchCount_DefaultsTo0Async() {
    var options = new TestTransportOptions();
    await Assert.That(options.MessagePrefetchCount).IsEqualTo(0);
  }

  // ========================================
  // DEFAULT VALUES — Reliability & Dead-Lettering
  // ========================================

  [Test]
  public async Task FailedMessageRetryLimit_DefaultsTo10Async() {
    var options = new TestTransportOptions();
    await Assert.That(options.FailedMessageRetryLimit).IsEqualTo(10);
  }

  [Test]
  public async Task AutoProvisionDeadLetterInfrastructure_DefaultsToTrueAsync() {
    var options = new TestTransportOptions();
    await Assert.That(options.AutoProvisionDeadLetterInfrastructure).IsTrue();
  }

  // ========================================
  // DEFAULT VALUES — Ordering
  // ========================================

  [Test]
  public async Task EnableOrderedDelivery_DefaultsToTrueAsync() {
    var options = new TestTransportOptions();
    await Assert.That(options.EnableOrderedDelivery).IsTrue();
  }

  [Test]
  public async Task ConcurrentOrderedStreams_DefaultsTo64Async() {
    var options = new TestTransportOptions();
    await Assert.That(options.ConcurrentOrderedStreams).IsEqualTo(64);
  }

  // ========================================
  // DEFAULT VALUES — Infrastructure
  // ========================================

  [Test]
  public async Task AutoProvisionInfrastructure_DefaultsToTrueAsync() {
    var options = new TestTransportOptions();
    await Assert.That(options.AutoProvisionInfrastructure).IsTrue();
  }

  // ========================================
  // DEFAULT VALUES — Connection Retry
  // ========================================

  [Test]
  public async Task InitialConnectionRetryAttempts_DefaultsTo5Async() {
    var options = new TestTransportOptions();
    await Assert.That(options.InitialConnectionRetryAttempts).IsEqualTo(5);
  }

  [Test]
  public async Task InitialConnectionRetryDelay_DefaultsTo1SecondAsync() {
    var options = new TestTransportOptions();
    await Assert.That(options.InitialConnectionRetryDelay).IsEqualTo(TimeSpan.FromSeconds(1));
  }

  [Test]
  public async Task MaxConnectionRetryDelay_DefaultsTo120SecondsAsync() {
    var options = new TestTransportOptions();
    await Assert.That(options.MaxConnectionRetryDelay).IsEqualTo(TimeSpan.FromSeconds(120));
  }

  [Test]
  public async Task ConnectionRetryBackoffMultiplier_DefaultsTo2Async() {
    var options = new TestTransportOptions();
    await Assert.That(options.ConnectionRetryBackoffMultiplier).IsEqualTo(2.0);
  }

  [Test]
  public async Task RetryConnectionIndefinitely_DefaultsToTrueAsync() {
    var options = new TestTransportOptions();
    await Assert.That(options.RetryConnectionIndefinitely).IsTrue();
  }

  // ========================================
  // SUBCLASS CAN OVERRIDE DEFAULTS
  // ========================================

  [Test]
  public async Task Subclass_CanOverrideEnableOrderedDeliveryDefault_ToFalseAsync() {
    var options = new OverriddenDefaultsOptions();
    await Assert.That(options.EnableOrderedDelivery).IsFalse();
  }

  [Test]
  public async Task Subclass_CanOverrideMessagePrefetchCountDefault_To10Async() {
    var options = new OverriddenDefaultsOptions();
    await Assert.That(options.MessagePrefetchCount).IsEqualTo(10);
  }

  [Test]
  public async Task Subclass_InheritsUnchangedDefaultsAsync() {
    // Overridden subclass should still have base defaults for properties it doesn't touch
    var options = new OverriddenDefaultsOptions();
    await Assert.That(options.ConcurrentMessageLimit).IsEqualTo(10);
    await Assert.That(options.FailedMessageRetryLimit).IsEqualTo(10);
    await Assert.That(options.ConcurrentOrderedStreams).IsEqualTo(64);
    await Assert.That(options.InitialConnectionRetryAttempts).IsEqualTo(5);
  }

  // ========================================
  // ALL PROPERTIES ARE SETTABLE
  // ========================================

  [Test]
  public async Task AllProperties_AreSettableAsync() {
    var options = new TestTransportOptions {
      ConcurrentMessageLimit = 42,
      MessagePrefetchCount = 100,
      FailedMessageRetryLimit = 25,
      AutoProvisionDeadLetterInfrastructure = false,
      EnableOrderedDelivery = false,
      ConcurrentOrderedStreams = 128,
      AutoProvisionInfrastructure = false,
      InitialConnectionRetryAttempts = 3,
      InitialConnectionRetryDelay = TimeSpan.FromSeconds(5),
      MaxConnectionRetryDelay = TimeSpan.FromMinutes(10),
      ConnectionRetryBackoffMultiplier = 1.5,
      RetryConnectionIndefinitely = false
    };

    await Assert.That(options.ConcurrentMessageLimit).IsEqualTo(42);
    await Assert.That(options.MessagePrefetchCount).IsEqualTo(100);
    await Assert.That(options.FailedMessageRetryLimit).IsEqualTo(25);
    await Assert.That(options.AutoProvisionDeadLetterInfrastructure).IsFalse();
    await Assert.That(options.EnableOrderedDelivery).IsFalse();
    await Assert.That(options.ConcurrentOrderedStreams).IsEqualTo(128);
    await Assert.That(options.AutoProvisionInfrastructure).IsFalse();
    await Assert.That(options.InitialConnectionRetryAttempts).IsEqualTo(3);
    await Assert.That(options.InitialConnectionRetryDelay).IsEqualTo(TimeSpan.FromSeconds(5));
    await Assert.That(options.MaxConnectionRetryDelay).IsEqualTo(TimeSpan.FromMinutes(10));
    await Assert.That(options.ConnectionRetryBackoffMultiplier).IsEqualTo(1.5);
    await Assert.That(options.RetryConnectionIndefinitely).IsFalse();
  }

  // ========================================
  // CAPABILITY-GATED VALIDATION
  // ========================================

  [Test]
  public async Task ValidateForCapabilities_NoWarnings_WhenAllCapabilitiesPresentAsync() {
    var options = new TestTransportOptions {
      FailedMessageRetryLimit = 25,
      ConcurrentMessageLimit = 20,
      EnableOrderedDelivery = true,
      ConcurrentOrderedStreams = 128
    };
    var logger = new CapturingLogger();

    options.ValidateForCapabilities(TransportCapabilities.All, logger);

    await Assert.That(logger.Warnings).IsEmpty();
  }

  [Test]
  public async Task ValidateForCapabilities_WarnsOnFailedMessageRetryLimit_WhenNotReliableAsync() {
    var options = new TestTransportOptions {
      FailedMessageRetryLimit = 25  // non-default
    };
    var logger = new CapturingLogger();

    options.ValidateForCapabilities(TransportCapabilities.PublishSubscribe, logger);

    await Assert.That(logger.Warnings).Contains(w => w.Contains("FailedMessageRetryLimit"));
  }

  [Test]
  public async Task ValidateForCapabilities_NoWarningOnFailedMessageRetryLimit_WhenDefaultAsync() {
    var options = new TestTransportOptions();  // default FailedMessageRetryLimit = 10
    var logger = new CapturingLogger();

    options.ValidateForCapabilities(TransportCapabilities.PublishSubscribe, logger);

    // Default value should NOT trigger a warning even without Reliable capability
    var retryWarnings = logger.Warnings.Where(w => w.Contains("FailedMessageRetryLimit")).ToList();
    await Assert.That(retryWarnings).IsEmpty();
  }

  [Test]
  public async Task ValidateForCapabilities_WarnsOnConcurrentMessageLimit_WhenNotPublishSubscribeAsync() {
    var options = new TestTransportOptions {
      ConcurrentMessageLimit = 42  // non-default
    };
    var logger = new CapturingLogger();

    options.ValidateForCapabilities(TransportCapabilities.Reliable, logger);

    await Assert.That(logger.Warnings).Contains(w => w.Contains("ConcurrentMessageLimit"));
  }

  [Test]
  public async Task ValidateForCapabilities_WarnsOnMessagePrefetchCount_WhenNotPublishSubscribeAsync() {
    var options = new TestTransportOptions {
      MessagePrefetchCount = 50  // non-default
    };
    var logger = new CapturingLogger();

    options.ValidateForCapabilities(TransportCapabilities.Reliable, logger);

    await Assert.That(logger.Warnings).Contains(w => w.Contains("MessagePrefetchCount"));
  }

  [Test]
  public async Task ValidateForCapabilities_WarnsOnEnableOrderedDelivery_WhenNotOrderedAsync() {
    // EnableOrderedDelivery defaults to true, so even without explicit set,
    // if the transport doesn't support Ordered, it should warn
    var options = new TestTransportOptions();
    var logger = new CapturingLogger();

    options.ValidateForCapabilities(TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable, logger);

    await Assert.That(logger.Warnings).Contains(w => w.Contains("EnableOrderedDelivery"));
  }

  [Test]
  public async Task ValidateForCapabilities_NoWarningOnEnableOrderedDelivery_WhenFalseAndNotOrderedAsync() {
    var options = new TestTransportOptions {
      EnableOrderedDelivery = false
    };
    var logger = new CapturingLogger();

    options.ValidateForCapabilities(TransportCapabilities.PublishSubscribe, logger);

    // EnableOrderedDelivery = false shouldn't warn even if transport doesn't support ordering
    var orderWarnings = logger.Warnings.Where(w => w.Contains("EnableOrderedDelivery")).ToList();
    await Assert.That(orderWarnings).IsEmpty();
  }

  [Test]
  public async Task ValidateForCapabilities_WarnsOnConcurrentOrderedStreams_WhenNotOrderedAsync() {
    var options = new TestTransportOptions {
      ConcurrentOrderedStreams = 128  // non-default
    };
    var logger = new CapturingLogger();

    options.ValidateForCapabilities(TransportCapabilities.PublishSubscribe, logger);

    await Assert.That(logger.Warnings).Contains(w => w.Contains("ConcurrentOrderedStreams"));
  }

  [Test]
  public async Task ValidateForCapabilities_WarnsOnAutoProvisionDeadLetter_WhenNotReliableAsync() {
    var options = new TestTransportOptions {
      AutoProvisionDeadLetterInfrastructure = false  // non-default
    };
    var logger = new CapturingLogger();

    options.ValidateForCapabilities(TransportCapabilities.PublishSubscribe, logger);

    await Assert.That(logger.Warnings).Contains(w => w.Contains("AutoProvisionDeadLetterInfrastructure"));
  }

  [Test]
  public async Task ValidateForCapabilities_DoesNotThrow_WhenLoggerIsNullAsync() {
    var options = new TestTransportOptions {
      FailedMessageRetryLimit = 99
    };

    // Should not throw even without a logger — silently skip warnings
    var exception = Record.Exception(() => options.ValidateForCapabilities(TransportCapabilities.None, null));

    await Assert.That(exception).IsNull();
  }

  private static class Record {
    public static Exception? Exception(Action action) {
      try {
        action();
        return null;
      } catch (Exception ex) {
        return ex;
      }
    }
  }

  [Test]
  public async Task ValidateForCapabilities_MultipleWarnings_WhenMultipleSettingsMisconfiguredAsync() {
    var options = new TestTransportOptions {
      FailedMessageRetryLimit = 25,
      ConcurrentMessageLimit = 42,
      MessagePrefetchCount = 50,
      ConcurrentOrderedStreams = 128
    };
    var logger = new CapturingLogger();

    // None = no capabilities at all
    options.ValidateForCapabilities(TransportCapabilities.None, logger);

    // Should warn about all non-default settings that don't have matching capabilities
    await Assert.That(logger.Warnings.Count).IsGreaterThanOrEqualTo(4);
  }

  // ========================================
  // ABSTRACT CLASS CANNOT BE INSTANTIATED DIRECTLY
  // ========================================

  [Test]
  public async Task TransportOptions_IsAbstractAsync() {
    await Assert.That(typeof(TransportOptions).IsAbstract).IsTrue();
  }

  // ========================================
  // TEST HELPER: Logger that captures warnings
  // ========================================

  private sealed class CapturingLogger : ILogger {
    public List<string> Warnings { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (logLevel == LogLevel.Warning) {
        Warnings.Add(formatter(state, exception));
      }
    }
  }
}
