using Microsoft.Extensions.Logging;
using Whizbang.Core.Configuration;
using Whizbang.Core.Validation;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Validation;

/// <summary>
/// Tests for GuidOrderingValidator - runtime validation of GUID time-ordering.
/// Verifies that TrackedGuid values are validated for time-ordering requirements
/// and appropriate actions are taken based on configuration.
/// </summary>
[Category("Validation")]
[Category("GuidOrdering")]
public class GuidOrderingValidatorTests {

  // ========================================
  // Time-Ordered GUID Tests
  // ========================================

  /// <summary>
  /// Test that v7 GUIDs (time-ordered) pass validation without warnings.
  /// </summary>
  [Test]
  public async Task ValidateForTimeOrdering_WithV7Guid_PassesValidationAsync() {
    // Arrange
    var options = new WhizbangOptions();
    var logger = new TestLogger();
    var validator = new GuidOrderingValidator(options, logger);
    var trackedGuid = TrackedGuid.NewMedo(); // v7 GUID

    // Act
    validator.ValidateForTimeOrdering(trackedGuid, "TestContext");

    // Assert - no warnings should be logged
    await Assert.That(logger.LoggedMessages).IsEmpty();
  }

  /// <summary>
  /// Test that Microsoft v7 GUIDs pass validation.
  /// </summary>
  [Test]
  public async Task ValidateForTimeOrdering_WithMicrosoftV7Guid_PassesValidationAsync() {
    // Arrange
    var options = new WhizbangOptions();
    var logger = new TestLogger();
    var validator = new GuidOrderingValidator(options, logger);
    var trackedGuid = TrackedGuid.NewMicrosoftV7();

    // Act
    validator.ValidateForTimeOrdering(trackedGuid, "TestContext");

    // Assert - no warnings should be logged
    await Assert.That(logger.LoggedMessages).IsEmpty();
  }

  // ========================================
  // Non-Time-Ordered GUID Tests
  // ========================================

  /// <summary>
  /// Test that v4 GUIDs (non-time-ordered) trigger warning by default.
  /// </summary>
  [Test]
  public async Task ValidateForTimeOrdering_WithV4Guid_LogsWarningByDefaultAsync() {
    // Arrange
    var options = new WhizbangOptions(); // Default severity is Warning
    var logger = new TestLogger();
    var validator = new GuidOrderingValidator(options, logger);
    var trackedGuid = TrackedGuid.NewRandom(); // v4 GUID

    // Act
    validator.ValidateForTimeOrdering(trackedGuid, "TestContext");

    // Assert
    await Assert.That(logger.LoggedMessages).Count().IsEqualTo(1);
    await Assert.That(logger.LoggedMessages[0].Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(logger.LoggedMessages[0].Message).Contains("TestContext");
    await Assert.That(logger.LoggedMessages[0].Message).Contains("Non-time-ordered");
  }

  /// <summary>
  /// Test that external GUIDs (source unknown) trigger warning.
  /// </summary>
  [Test]
  public async Task ValidateForTimeOrdering_WithExternalGuid_LogsWarningAsync() {
    // Arrange
    var options = new WhizbangOptions();
    var logger = new TestLogger();
    var validator = new GuidOrderingValidator(options, logger);
    var trackedGuid = TrackedGuid.FromExternal(Guid.NewGuid());

    // Act
    validator.ValidateForTimeOrdering(trackedGuid, "EventId");

    // Assert
    await Assert.That(logger.LoggedMessages).Count().IsEqualTo(1);
    await Assert.That(logger.LoggedMessages[0].Level).IsEqualTo(LogLevel.Warning);
  }

  // ========================================
  // Severity Configuration Tests
  // ========================================

  /// <summary>
  /// Test that severity=Error throws GuidOrderingException.
  /// </summary>
  [Test]
  public async Task ValidateForTimeOrdering_WithSeverityError_ThrowsExceptionAsync() {
    // Arrange
    var options = new WhizbangOptions {
      GuidOrderingViolationSeverity = GuidOrderingSeverity.Error
    };
    var logger = new TestLogger();
    var validator = new GuidOrderingValidator(options, logger);
    var trackedGuid = TrackedGuid.NewRandom(); // v4 GUID

    // Act & Assert
    var act = () => validator.ValidateForTimeOrdering(trackedGuid, "TestContext");
    await Assert.That(act).Throws<GuidOrderingException>();
  }

  /// <summary>
  /// Test that severity=Error also logs error before throwing.
  /// </summary>
  [Test]
  public async Task ValidateForTimeOrdering_WithSeverityError_LogsErrorAsync() {
    // Arrange
    var options = new WhizbangOptions {
      GuidOrderingViolationSeverity = GuidOrderingSeverity.Error
    };
    var logger = new TestLogger();
    var validator = new GuidOrderingValidator(options, logger);
    var trackedGuid = TrackedGuid.NewRandom();

    // Act
    try {
      validator.ValidateForTimeOrdering(trackedGuid, "TestContext");
    } catch (GuidOrderingException) {
      // Expected
    }

    // Assert
    await Assert.That(logger.LoggedMessages).Count().IsEqualTo(1);
    await Assert.That(logger.LoggedMessages[0].Level).IsEqualTo(LogLevel.Error);
  }

  /// <summary>
  /// Test that severity=Info logs at Info level.
  /// </summary>
  [Test]
  public async Task ValidateForTimeOrdering_WithSeverityInfo_LogsInfoAsync() {
    // Arrange
    var options = new WhizbangOptions {
      GuidOrderingViolationSeverity = GuidOrderingSeverity.Info
    };
    var logger = new TestLogger();
    var validator = new GuidOrderingValidator(options, logger);
    var trackedGuid = TrackedGuid.NewRandom();

    // Act
    validator.ValidateForTimeOrdering(trackedGuid, "TestContext");

    // Assert
    await Assert.That(logger.LoggedMessages).Count().IsEqualTo(1);
    await Assert.That(logger.LoggedMessages[0].Level).IsEqualTo(LogLevel.Information);
  }

  /// <summary>
  /// Test that severity=None suppresses all validation.
  /// </summary>
  [Test]
  public async Task ValidateForTimeOrdering_WithSeverityNone_NoLoggingAsync() {
    // Arrange
    var options = new WhizbangOptions {
      GuidOrderingViolationSeverity = GuidOrderingSeverity.None
    };
    var logger = new TestLogger();
    var validator = new GuidOrderingValidator(options, logger);
    var trackedGuid = TrackedGuid.NewRandom();

    // Act
    validator.ValidateForTimeOrdering(trackedGuid, "TestContext");

    // Assert - nothing logged
    await Assert.That(logger.LoggedMessages).IsEmpty();
  }

  // ========================================
  // DisableGuidTracking Tests
  // ========================================

  /// <summary>
  /// Test that DisableGuidTracking=true bypasses all validation.
  /// </summary>
  [Test]
  public async Task ValidateForTimeOrdering_WithTrackingDisabled_BypassesValidationAsync() {
    // Arrange
    var options = new WhizbangOptions {
      DisableGuidTracking = true,
      GuidOrderingViolationSeverity = GuidOrderingSeverity.Error // Would throw if not bypassed
    };
    var logger = new TestLogger();
    var validator = new GuidOrderingValidator(options, logger);
    var trackedGuid = TrackedGuid.NewRandom();

    // Act - should not throw even with Error severity
    validator.ValidateForTimeOrdering(trackedGuid, "TestContext");

    // Assert - nothing logged
    await Assert.That(logger.LoggedMessages).IsEmpty();
  }

  // ========================================
  // Exception Message Tests
  // ========================================

  /// <summary>
  /// Test that GuidOrderingException contains useful information.
  /// </summary>
  [Test]
  public async Task GuidOrderingException_ContainsContextAndMetadataAsync() {
    // Arrange
    var options = new WhizbangOptions {
      GuidOrderingViolationSeverity = GuidOrderingSeverity.Error
    };
    var logger = new TestLogger();
    var validator = new GuidOrderingValidator(options, logger);
    var trackedGuid = TrackedGuid.NewRandom();

    // Act
    GuidOrderingException? caughtException = null;
    try {
      validator.ValidateForTimeOrdering(trackedGuid, "MyEventId");
    } catch (GuidOrderingException ex) {
      caughtException = ex;
    }

    // Assert
    await Assert.That(caughtException).IsNotNull();
    await Assert.That(caughtException!.Message).Contains("MyEventId");
  }

  // ========================================
  // Default Options Tests
  // ========================================

  /// <summary>
  /// Test that WhizbangOptions has correct defaults.
  /// </summary>
  [Test]
  public async Task WhizbangOptions_HasCorrectDefaultsAsync() {
    // Arrange & Act
    var options = new WhizbangOptions();

    // Assert
    await Assert.That(options.DisableGuidTracking).IsFalse();
    await Assert.That(options.GuidOrderingViolationSeverity).IsEqualTo(GuidOrderingSeverity.Warning);
  }

  // ========================================
  // Test Helper
  // ========================================

  /// <summary>
  /// Simple logger for capturing log messages in tests.
  /// </summary>
  private sealed class TestLogger : ILogger {
    public List<(LogLevel Level, string Message)> LoggedMessages { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
        NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) {
      LoggedMessages.Add((logLevel, formatter(state, exception)));
    }

    private sealed class NullScope : IDisposable {
      public static NullScope Instance { get; } = new();
      public void Dispose() { }
    }
  }
}
