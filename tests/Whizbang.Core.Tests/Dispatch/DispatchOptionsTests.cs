using TUnit.Core;
using Whizbang.Core.Dispatch;

namespace Whizbang.Core.Tests.Dispatch;

/// <summary>
/// Tests for DispatchOptions configuration.
/// Validates default values, fluent API, and validation behavior.
/// </summary>
public class DispatchOptionsTests {
  // ========================================
  // Default Value Tests
  // ========================================

  [Test]
  public async Task Default_CancellationToken_IsNone_Async() {
    // Arrange
    var options = new DispatchOptions();

    // Assert - Default should be CancellationToken.None
    await Assert.That(options.CancellationToken).IsEqualTo(CancellationToken.None);
  }

  [Test]
  public async Task Default_Timeout_IsNull_Async() {
    // Arrange
    var options = new DispatchOptions();

    // Assert - Default should be no timeout
    await Assert.That(options.Timeout).IsNull();
  }

  [Test]
  public async Task Default_StaticProperty_ReturnsNewInstanceEachTimeAsync() {
    // Act
    var options1 = DispatchOptions.Default;
    var options2 = DispatchOptions.Default;

    // Assert - Each call should return a new instance
    await Assert.That(options1).IsNotSameReferenceAs(options2);
  }

  // ========================================
  // Fluent API Tests
  // ========================================

  [Test]
  public async Task WithCancellationToken_SetsToken_ReturnsSelfAsync() {
    // Arrange
    using var cts = new CancellationTokenSource();
    var options = new DispatchOptions();

    // Act
    var result = options.WithCancellationToken(cts.Token);

    // Assert
    await Assert.That(options.CancellationToken).IsEqualTo(cts.Token);
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task WithTimeout_SetsTimeout_ReturnsSelfAsync() {
    // Arrange
    var timeout = TimeSpan.FromSeconds(30);
    var options = new DispatchOptions();

    // Act
    var result = options.WithTimeout(timeout);

    // Assert
    await Assert.That(options.Timeout).IsEqualTo(timeout);
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task WithTimeout_ZeroValue_SetsTimeoutAsync() {
    // Arrange
    var options = new DispatchOptions();

    // Act
    var result = options.WithTimeout(TimeSpan.Zero);

    // Assert - Zero is valid (immediate timeout)
    await Assert.That(options.Timeout).IsEqualTo(TimeSpan.Zero);
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task WithTimeout_NegativeValue_ThrowsArgumentOutOfRangeExceptionAsync() {
    // Arrange
    var options = new DispatchOptions();
    var negativeTimeout = TimeSpan.FromSeconds(-1);

    // Act & Assert
    await Assert.That(() => options.WithTimeout(negativeTimeout))
      .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task FluentApi_CanChainMultipleCalls_Async() {
    // Arrange
    using var cts = new CancellationTokenSource();
    var timeout = TimeSpan.FromMinutes(5);

    // Act
    var options = new DispatchOptions()
      .WithCancellationToken(cts.Token)
      .WithTimeout(timeout);

    // Assert
    await Assert.That(options.CancellationToken).IsEqualTo(cts.Token);
    await Assert.That(options.Timeout).IsEqualTo(timeout);
  }

  // ========================================
  // Property Setter Tests (for coverage)
  // ========================================

  [Test]
  public async Task CancellationToken_PropertySetter_WorksAsync() {
    // Arrange
    using var cts = new CancellationTokenSource();
    var options = new DispatchOptions {
      // Act
      CancellationToken = cts.Token
    };

    // Assert
    await Assert.That(options.CancellationToken).IsEqualTo(cts.Token);
  }

  [Test]
  public async Task Timeout_PropertySetter_WorksAsync() {
    // Arrange
    var timeout = TimeSpan.FromSeconds(10);
    var options = new DispatchOptions {
      // Act
      Timeout = timeout
    };

    // Assert
    await Assert.That(options.Timeout).IsEqualTo(timeout);
  }

  [Test]
  public async Task Timeout_PropertySetter_AcceptsNullAsync() {
    // Arrange
    var options = new DispatchOptions { Timeout = TimeSpan.FromSeconds(10) };

    // Act
    options.Timeout = null;

    // Assert
    await Assert.That(options.Timeout).IsNull();
  }

  // ========================================
  // WaitForPerspectives Tests
  // ========================================

  [Test]
  public async Task Default_WaitForPerspectives_IsFalseAsync() {
    // Arrange
    var options = new DispatchOptions();

    // Assert - Default should be false (don't wait for perspectives)
    await Assert.That(options.WaitForPerspectives).IsFalse();
  }

  [Test]
  public async Task Default_PerspectiveWaitTimeout_Is30SecondsAsync() {
    // Arrange
    var options = new DispatchOptions();

    // Assert - Default timeout should be 30 seconds
    await Assert.That(options.PerspectiveWaitTimeout).IsEqualTo(TimeSpan.FromSeconds(30));
  }

  [Test]
  public async Task WithPerspectiveWait_SetsWaitForPerspectivesToTrueAsync() {
    // Arrange
    var options = new DispatchOptions();

    // Act
    var result = options.WithPerspectiveWait();

    // Assert
    await Assert.That(options.WaitForPerspectives).IsTrue();
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task WithPerspectiveWait_WithTimeout_SetsTimeoutAsync() {
    // Arrange
    var options = new DispatchOptions();
    var customTimeout = TimeSpan.FromMinutes(2);

    // Act
    var result = options.WithPerspectiveWait(customTimeout);

    // Assert
    await Assert.That(options.WaitForPerspectives).IsTrue();
    await Assert.That(options.PerspectiveWaitTimeout).IsEqualTo(customTimeout);
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task WithPerspectiveWait_NoTimeout_KeepsDefaultTimeoutAsync() {
    // Arrange
    var options = new DispatchOptions();
    var defaultTimeout = options.PerspectiveWaitTimeout;

    // Act
    options.WithPerspectiveWait();

    // Assert - timeout should remain at default
    await Assert.That(options.PerspectiveWaitTimeout).IsEqualTo(defaultTimeout);
  }

  [Test]
  public async Task WaitForPerspectives_PropertySetter_WorksAsync() {
    // Arrange
    var options = new DispatchOptions {
      // Act
      WaitForPerspectives = true
    };

    // Assert
    await Assert.That(options.WaitForPerspectives).IsTrue();
  }

  [Test]
  public async Task PerspectiveWaitTimeout_PropertySetter_WorksAsync() {
    // Arrange
    var options = new DispatchOptions();
    var timeout = TimeSpan.FromMinutes(5);

    // Act
    options.PerspectiveWaitTimeout = timeout;

    // Assert
    await Assert.That(options.PerspectiveWaitTimeout).IsEqualTo(timeout);
  }

  [Test]
  public async Task FluentApi_CanChainWithPerspectiveWaitAsync() {
    // Arrange
    using var cts = new CancellationTokenSource();
    var timeout = TimeSpan.FromMinutes(5);
    var perspectiveTimeout = TimeSpan.FromMinutes(2);

    // Act
    var options = new DispatchOptions()
      .WithCancellationToken(cts.Token)
      .WithTimeout(timeout)
      .WithPerspectiveWait(perspectiveTimeout);

    // Assert
    await Assert.That(options.CancellationToken).IsEqualTo(cts.Token);
    await Assert.That(options.Timeout).IsEqualTo(timeout);
    await Assert.That(options.WaitForPerspectives).IsTrue();
    await Assert.That(options.PerspectiveWaitTimeout).IsEqualTo(perspectiveTimeout);
  }

  // ── RED-2: ScheduledFor forward-scheduling surface ───────────────────
  //
  // wh_outbox + wh_inbox already honor scheduled_for on the read side (migration 040
  // FetchOutboxInboxBatch, migration 049 NotifyScheduledRetryDue). What's missing is the
  // producer API: today only the retry-backoff SQL paths (017/018/019) set scheduled_for.
  // Surface it on DispatchOptions so consumers (e.g., Whizbang.Sagas' completion watchdog)
  // can request future-dated delivery without bypassing the dispatcher.

  [Test]
  public async Task ScheduledFor_PropertyExistsAsync() {
    var prop = typeof(DispatchOptions).GetProperty("ScheduledFor",
      System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

    await Assert.That(prop)
      .IsNotNull()
      .Because("Forward-scheduled delivery (watchdog ticks, deferred retries) needs an API surface on DispatchOptions. " +
               "wh_outbox.scheduled_for is honored downstream already; producer can't set it without this property.");
    await Assert.That(prop!.PropertyType)
      .IsEqualTo(typeof(DateTimeOffset?))
      .Because("Nullable DateTimeOffset mirrors OutboxRecord.ScheduledFor and lets callers omit (immediate dispatch) or set explicit UTC instant.");
    await Assert.That(prop.CanRead && prop.CanWrite)
      .IsTrue()
      .Because("ScheduledFor must be settable on the options instance (consumers chain via fluent or assign directly).");
  }

  [Test]
  public async Task ScheduledFor_DefaultIsNullAsync() {
    var options = new DispatchOptions();
    var prop = typeof(DispatchOptions).GetProperty("ScheduledFor",
      System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
    if (prop is null) {
      // Both REDs (this one and ScheduledFor_PropertyExistsAsync) fail until the property ships;
      // surface the gap here too so the failure inventory is complete.
      throw new InvalidOperationException("ScheduledFor property does not yet exist — see ScheduledFor_PropertyExistsAsync for the gap rationale.");
    }
    var value = prop.GetValue(options);
    await Assert.That(value)
      .IsNull()
      .Because("Default DispatchOptions == immediate dispatch. Null carries that semantic and matches every other producer call site that didn't opt in.");
  }

  [Test]
  public async Task WithScheduledFor_FluentMethodExistsAndChainsAsync() {
    var method = typeof(DispatchOptions).GetMethod("WithScheduledFor",
      System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
      binder: null,
      types: [typeof(DateTimeOffset)],
      modifiers: null);

    await Assert.That(method)
      .IsNotNull()
      .Because("DispatchOptions exposes a WithCancellationToken / WithTimeout / WithPerspectiveWait fluent shape. " +
               "WithScheduledFor(DateTimeOffset) is the matching producer API for the new scheduled-delivery surface.");
    await Assert.That(method!.ReturnType)
      .IsEqualTo(typeof(DispatchOptions))
      .Because("Fluent chaining requires returning DispatchOptions (matches WithCancellationToken's shape).");

    var options = new DispatchOptions();
    var scheduled = DateTimeOffset.UtcNow.AddMinutes(5);
    var returned = method.Invoke(options, [scheduled]);

    await Assert.That(returned)
      .IsSameReferenceAs(options)
      .Because("WithScheduledFor must return the same instance for chaining.");

    var prop = typeof(DispatchOptions).GetProperty("ScheduledFor",
      System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
    var readBack = prop!.GetValue(options);
    await Assert.That(readBack)
      .IsEqualTo((DateTimeOffset?)scheduled)
      .Because("WithScheduledFor must set the property to the supplied instant.");
  }
}
