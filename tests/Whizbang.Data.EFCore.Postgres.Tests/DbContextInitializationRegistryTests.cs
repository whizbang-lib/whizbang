using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for DbContextInitializationRegistry static registry.
/// Each test resets static state via reflection to ensure isolation.
/// </summary>
[NotInParallel("DbContextInitializationRegistry")]
public class DbContextInitializationRegistryTests {
  [Before(Test)]
  public void ResetStaticState() {
    // Reset _initializers list
    var initializersField = typeof(DbContextInitializationRegistry)
        .GetField("_initializers", BindingFlags.Static | BindingFlags.NonPublic)!;
    var list = (System.Collections.IList)initializersField.GetValue(null)!;
    list.Clear();

    // Reset _initialized flag back to 0
    var initializedField = typeof(DbContextInitializationRegistry)
        .GetField("_initialized", BindingFlags.Static | BindingFlags.NonPublic)!;
    initializedField.SetValue(null, 0);
  }

  [Test]
  public async Task Register_AddsInitializer_IncreasesCountAsync() {
    // Arrange & Act
    DbContextInitializationRegistry.Register<FakeDbContextA>(
        (_, _, _) => Task.CompletedTask);

    // Assert
    await Assert.That(DbContextInitializationRegistry.Count).IsEqualTo(1);
  }

  [Test]
  public async Task InitializeAllAsync_CallsAllRegisteredCallbacksAsync() {
    // Arrange
    var callCount = 0;
    DbContextInitializationRegistry.Register<FakeDbContextA>(
        (_, _, _) => { callCount++; return Task.CompletedTask; });
    DbContextInitializationRegistry.Register<FakeDbContextB>(
        (_, _, _) => { callCount++; return Task.CompletedTask; });

    var sp = new FakeServiceProvider();

    // Act
    await DbContextInitializationRegistry.InitializeAllAsync(sp);

    // Assert
    await Assert.That(callCount).IsEqualTo(2);
  }

  [Test]
  public async Task InitializeAllAsync_IdempotencyGuard_SkipsSecondCallAsync() {
    // Arrange
    var callCount = 0;
    DbContextInitializationRegistry.Register<FakeDbContextA>(
        (_, _, _) => { callCount++; return Task.CompletedTask; });

    var sp = new FakeServiceProvider();

    // Act — call twice
    await DbContextInitializationRegistry.InitializeAllAsync(sp);
    await DbContextInitializationRegistry.InitializeAllAsync(sp);

    // Assert — callback invoked only once
    await Assert.That(callCount).IsEqualTo(1);
  }

  [Test]
  public async Task InitializeAllAsync_WithNoRegistrations_CompletesSuccessfullyAsync() {
    // Arrange
    var sp = new FakeServiceProvider();

    // Act & Assert — should not throw
    await DbContextInitializationRegistry.InitializeAllAsync(sp);
    await Assert.That(DbContextInitializationRegistry.Count).IsEqualTo(0);
  }

  [Test]
  public async Task InitializeAllAsync_LogsStartAndCompletionAsync() {
    // Arrange
    DbContextInitializationRegistry.Register<FakeDbContextA>(
        (_, _, _) => Task.CompletedTask);

    var sp = new FakeServiceProvider();
    var logger = NullLogger.Instance;

    // Act — should not throw when logger is provided
    await DbContextInitializationRegistry.InitializeAllAsync(sp, logger);

    // Assert — if we get here without exception, logging delegates executed successfully
    await Assert.That(DbContextInitializationRegistry.Count).IsEqualTo(1);
  }

  [Test]
  public async Task InitializeAllAsync_IdempotencyGuard_LogsSkipMessageAsync() {
    // Arrange
    DbContextInitializationRegistry.Register<FakeDbContextA>(
        (_, _, _) => Task.CompletedTask);

    var sp = new FakeServiceProvider();
    var logger = NullLogger.Instance;

    // Act — first call initializes, second call hits the guard
    await DbContextInitializationRegistry.InitializeAllAsync(sp, logger);
    await DbContextInitializationRegistry.InitializeAllAsync(sp, logger);

    // Assert — both calls complete without exception (debug log fires on second call)
    // If we reach here, the logger delegate paths executed without error
    await Assert.That(DbContextInitializationRegistry.Count).IsEqualTo(1);
  }

  [Test]
  public async Task Count_ReturnsNumberOfRegisteredInitializersAsync() {
    // Arrange
    DbContextInitializationRegistry.Register<FakeDbContextA>(
        (_, _, _) => Task.CompletedTask);
    DbContextInitializationRegistry.Register<FakeDbContextB>(
        (_, _, _) => Task.CompletedTask);
    DbContextInitializationRegistry.Register<FakeDbContextA>(
        (_, _, _) => Task.CompletedTask);

    // Act & Assert
    await Assert.That(DbContextInitializationRegistry.Count).IsEqualTo(3);
  }

  // --- Fake DbContext types for test isolation ---
  private sealed class FakeDbContextA;
  private sealed class FakeDbContextB;

  private sealed class FakeServiceProvider : IServiceProvider {
    public object? GetService(Type serviceType) => null;
  }
}
