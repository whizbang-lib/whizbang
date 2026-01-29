using System.Text.Json;
using ECommerce.BFF.API.Audit;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Audit;
using Whizbang.Core.Tags;

namespace ECommerce.BFF.API.Tests.Audit;

/// <summary>
/// Unit tests for <see cref="AuditTagHook"/>.
/// Tests the real-time logging hook for audit events.
/// </summary>
public class AuditTagHookTests {
  #region Constructor Tests

  [Test]
  public async Task Constructor_WithLogger_DoesNotThrowAsync() {
    // Arrange
    var logger = new MockLogger<AuditTagHook>();

    // Act & Assert
    await Assert.That(() => new AuditTagHook(logger))
        .ThrowsNothing();
  }

  #endregion

  #region OnTaggedMessageAsync Tests

  [Test]
  public async Task OnTaggedMessageAsync_LogsAuditEventAsync() {
    // Arrange
    var logger = new MockLogger<AuditTagHook>();
    var hook = new AuditTagHook(logger);

    var attribute = new AuditEventAttribute {
      Level = AuditLevel.Info,
      Reason = "Test Reason"
    };

    var context = new TagContext<AuditEventAttribute> {
      Attribute = attribute,
      Message = new TestEvent { Name = "TestEvent" },
      MessageType = typeof(TestEvent),
      Payload = JsonSerializer.SerializeToElement(new { Name = "TestEvent" }),
      Scope = null
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert - Returns null to pass original payload
    await Assert.That(result).IsNull();

    // Assert - Logger was called
    await Assert.That(logger.LoggedMessages).Count().IsGreaterThanOrEqualTo(1);
    await Assert.That(logger.LoggedMessages[0]).Contains("TestEvent");
    await Assert.That(logger.LoggedMessages[0]).Contains("Test Reason");
  }

  [Test]
  public async Task OnTaggedMessageAsync_WithScope_ExtractsTenantIdAsync() {
    // Arrange
    var logger = new MockLogger<AuditTagHook>();
    var hook = new AuditTagHook(logger);

    var attribute = new AuditEventAttribute {
      Level = AuditLevel.Warning,
      Reason = "Tenant Access"
    };

    var scope = new Dictionary<string, object?> {
      ["TenantId"] = "tenant-123",
      ["UserId"] = "user-456"
    };

    var context = new TagContext<AuditEventAttribute> {
      Attribute = attribute,
      Message = new TestEvent { Name = "TenantEvent" },
      MessageType = typeof(TestEvent),
      Payload = JsonSerializer.SerializeToElement(new { Name = "TenantEvent" }),
      Scope = scope
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
    await Assert.That(logger.LoggedMessages).Count().IsGreaterThanOrEqualTo(1);
    await Assert.That(logger.LoggedMessages[0]).Contains("tenant-123");
    await Assert.That(logger.LoggedMessages[0]).Contains("user-456");
  }

  [Test]
  public async Task OnTaggedMessageAsync_WithNullScope_LogsNAValuesAsync() {
    // Arrange
    var logger = new MockLogger<AuditTagHook>();
    var hook = new AuditTagHook(logger);

    var attribute = new AuditEventAttribute {
      Level = AuditLevel.Info
    };

    var context = new TagContext<AuditEventAttribute> {
      Attribute = attribute,
      Message = new TestEvent { Name = "NoScopeEvent" },
      MessageType = typeof(TestEvent),
      Payload = JsonSerializer.SerializeToElement(new { Name = "NoScopeEvent" }),
      Scope = null // No scope
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert - Should contain N/A for missing values
    await Assert.That(result).IsNull();
    await Assert.That(logger.LoggedMessages).Count().IsGreaterThanOrEqualTo(1);
    await Assert.That(logger.LoggedMessages[0]).Contains("N/A");
  }

  [Test]
  public async Task OnTaggedMessageAsync_WithNullReason_LogsNAAsync() {
    // Arrange
    var logger = new MockLogger<AuditTagHook>();
    var hook = new AuditTagHook(logger);

    var attribute = new AuditEventAttribute {
      Level = AuditLevel.Info,
      Reason = null // No reason
    };

    var context = new TagContext<AuditEventAttribute> {
      Attribute = attribute,
      Message = new TestEvent { Name = "NoReasonEvent" },
      MessageType = typeof(TestEvent),
      Payload = JsonSerializer.SerializeToElement(new { Name = "NoReasonEvent" }),
      Scope = null
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert - Should log N/A for reason
    await Assert.That(result).IsNull();
    await Assert.That(logger.LoggedMessages).Count().IsGreaterThanOrEqualTo(1);
    await Assert.That(logger.LoggedMessages[0]).Contains("N/A");
  }

  [Test]
  public async Task OnTaggedMessageAsync_WithWarningLevel_LogsWarningLevelAsync() {
    // Arrange
    var logger = new MockLogger<AuditTagHook>();
    var hook = new AuditTagHook(logger);

    var attribute = new AuditEventAttribute {
      Level = AuditLevel.Warning,
      Reason = "Suspicious Activity"
    };

    var context = new TagContext<AuditEventAttribute> {
      Attribute = attribute,
      Message = new TestEvent { Name = "WarningEvent" },
      MessageType = typeof(TestEvent),
      Payload = JsonSerializer.SerializeToElement(new { Name = "WarningEvent" }),
      Scope = null
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
    await Assert.That(logger.LoggedMessages).Count().IsGreaterThanOrEqualTo(1);
    await Assert.That(logger.LoggedMessages[0]).Contains("Warning");
  }

  [Test]
  public async Task OnTaggedMessageAsync_WithCriticalLevel_LogsCriticalLevelAsync() {
    // Arrange
    var logger = new MockLogger<AuditTagHook>();
    var hook = new AuditTagHook(logger);

    var attribute = new AuditEventAttribute {
      Level = AuditLevel.Critical,
      Reason = "Security Breach"
    };

    var context = new TagContext<AuditEventAttribute> {
      Attribute = attribute,
      Message = new TestEvent { Name = "ErrorEvent" },
      MessageType = typeof(TestEvent),
      Payload = JsonSerializer.SerializeToElement(new { Name = "ErrorEvent" }),
      Scope = null
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
    await Assert.That(logger.LoggedMessages).Count().IsGreaterThanOrEqualTo(1);
    await Assert.That(logger.LoggedMessages[0]).Contains("Critical");
  }

  [Test]
  public async Task OnTaggedMessageAsync_WithScopeButMissingKeys_LogsNAAsync() {
    // Arrange
    var logger = new MockLogger<AuditTagHook>();
    var hook = new AuditTagHook(logger);

    var attribute = new AuditEventAttribute {
      Level = AuditLevel.Info,
      Reason = "Test"
    };

    var scope = new Dictionary<string, object?> {
      ["OtherKey"] = "some-value" // No TenantId or UserId
    };

    var context = new TagContext<AuditEventAttribute> {
      Attribute = attribute,
      Message = new TestEvent { Name = "PartialScopeEvent" },
      MessageType = typeof(TestEvent),
      Payload = JsonSerializer.SerializeToElement(new { Name = "PartialScopeEvent" }),
      Scope = scope
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
    await Assert.That(logger.LoggedMessages).Count().IsGreaterThanOrEqualTo(1);
    // TenantId and UserId should be N/A
    await Assert.That(logger.LoggedMessages[0]).Contains("N/A");
  }

  [Test]
  public async Task OnTaggedMessageAsync_WithCancellationToken_CompletesAsync() {
    // Arrange
    var logger = new MockLogger<AuditTagHook>();
    var hook = new AuditTagHook(logger);

    var attribute = new AuditEventAttribute {
      Level = AuditLevel.Info
    };

    var context = new TagContext<AuditEventAttribute> {
      Attribute = attribute,
      Message = new TestEvent { Name = "CancellableEvent" },
      MessageType = typeof(TestEvent),
      Payload = JsonSerializer.SerializeToElement(new { Name = "CancellableEvent" }),
      Scope = null
    };

    using var cts = new CancellationTokenSource();

    // Act
    var result = await hook.OnTaggedMessageAsync(context, cts.Token);

    // Assert
    await Assert.That(result).IsNull();
  }

  #endregion

  #region Test Types

  private sealed record TestEvent {
    public required string Name { get; init; }
  }

  #endregion

  #region Mock Logger

  /// <summary>
  /// Simple mock logger that captures log messages.
  /// </summary>
  private sealed class MockLogger<T> : ILogger<T> {
    public List<string> LoggedMessages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) {
      LoggedMessages.Add(formatter(state, exception));
    }
  }

  #endregion
}
