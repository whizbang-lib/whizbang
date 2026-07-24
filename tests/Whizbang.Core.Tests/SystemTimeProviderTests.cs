using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// Tests for <see cref="SystemTimeProvider"/> and <see cref="ITimeProvider"/>.
/// Target: 100% line and branch coverage.
/// </summary>
public class SystemTimeProviderTests {
  #region Constructor Tests

  [Test]
  public async Task Constructor_Default_UsesSystemTimeProviderAsync() {
    // Arrange & Act
    var provider = new SystemTimeProvider();

    // Assert - should return a time close to now (within 1 second)
    var now = DateTimeOffset.UtcNow;
    var result = provider.GetUtcNow();
    var difference = (result - now).Duration();

    await Assert.That(difference).IsLessThan(TimeSpan.FromSeconds(1));
  }

  [Test]
  public async Task Constructor_WithCustomTimeProvider_UsesThatProviderAsync() {
    // Arrange
    var fakeTime = new FakeTimeProvider(new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero));
    var provider = new SystemTimeProvider(fakeTime);

    // Act
    var result = provider.GetUtcNow();

    // Assert
    await Assert.That(result.Year).IsEqualTo(2025);
    await Assert.That(result.Month).IsEqualTo(6);
    await Assert.That(result.Day).IsEqualTo(15);
    await Assert.That(result.Hour).IsEqualTo(12);
  }

  [Test]
  public async Task Constructor_WithNullTimeProvider_ThrowsArgumentNullExceptionAsync() {
    // Arrange & Act & Assert
    SystemTimeProvider action() => new(null!);

    await Assert.That(action).ThrowsExactly<ArgumentNullException>()
        .WithParameterName("timeProvider");
  }

  #endregion

  #region GetUtcNow Tests

  [Test]
  public async Task GetUtcNow_WithFakeTimeProvider_ReturnsExpectedTimeAsync() {
    // Arrange
    var expectedTime = new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.Zero);
    var fakeTime = new FakeTimeProvider(expectedTime);
    var provider = new SystemTimeProvider(fakeTime);

    // Act
    var result = provider.GetUtcNow();

    // Assert
    await Assert.That(result).IsEqualTo(expectedTime);
  }

  [Test]
  public async Task GetUtcNow_AfterAdvancingFakeTime_ReturnsAdvancedTimeAsync() {
    // Arrange
    var startTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    var fakeTime = new FakeTimeProvider(startTime);
    var provider = new SystemTimeProvider(fakeTime);

    // Act
    fakeTime.Advance(TimeSpan.FromHours(5));
    var result = provider.GetUtcNow();

    // Assert
    await Assert.That(result).IsEqualTo(startTime.AddHours(5));
  }

  #endregion

  #region GetLocalNow Tests

  [Test]
  public async Task GetLocalNow_WithFakeTimeProvider_ReturnsLocalTimeAsync() {
    // Arrange
    var utcTime = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
    var fakeTime = new FakeTimeProvider(utcTime);
    var provider = new SystemTimeProvider(fakeTime);

    // Act
    var result = provider.GetLocalNow();

    // Assert - FakeTimeProvider returns UTC for local time by default
    // DateTimeOffset is a value type, so we verify it has a meaningful value
    await Assert.That(result.Year).IsGreaterThan(2000);
  }

  #endregion

  #region GetTimestamp Tests

  [Test]
  public async Task GetTimestamp_CalledTwice_SecondIsGreaterOrEqualAsync() {
    // Arrange
    var provider = new SystemTimeProvider();

    // Act
    var first = provider.GetTimestamp();
    var second = provider.GetTimestamp();

    // Assert
    await Assert.That(second).IsGreaterThanOrEqualTo(first);
  }

  [Test]
  public async Task GetTimestamp_WithFakeTimeProvider_ReturnsValidTimestampAsync() {
    // Arrange
    var fakeTime = new FakeTimeProvider();
    var provider = new SystemTimeProvider(fakeTime);

    // Act
    var timestamp = provider.GetTimestamp();

    // Assert
    await Assert.That(timestamp).IsGreaterThanOrEqualTo(0);
  }

  #endregion

  #region GetElapsedTime(long) Tests

  [Test]
  public async Task GetElapsedTime_WithStartingTimestamp_ReturnsElapsedTimeAsync() {
    // Arrange
    var fakeTime = new FakeTimeProvider();
    var provider = new SystemTimeProvider(fakeTime);
    var start = provider.GetTimestamp();

    // Act
    fakeTime.Advance(TimeSpan.FromMilliseconds(500));
    var elapsed = provider.GetElapsedTime(start);

    // Assert
    await Assert.That(elapsed).IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(400));
    await Assert.That(elapsed).IsLessThanOrEqualTo(TimeSpan.FromMilliseconds(600));
  }

  #endregion

  #region GetElapsedTime(long, long) Tests

  [Test]
  public async Task GetElapsedTime_WithStartAndEndTimestamp_ReturnsElapsedTimeAsync() {
    // Arrange
    var fakeTime = new FakeTimeProvider();
    var provider = new SystemTimeProvider(fakeTime);
    var start = provider.GetTimestamp();

    fakeTime.Advance(TimeSpan.FromSeconds(2));
    var end = provider.GetTimestamp();

    // Act
    var elapsed = provider.GetElapsedTime(start, end);

    // Assert
    await Assert.That(elapsed).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(1.9));
    await Assert.That(elapsed).IsLessThanOrEqualTo(TimeSpan.FromSeconds(2.1));
  }

  #endregion

  #region TimestampFrequency Tests

  [Test]
  public async Task TimestampFrequency_ReturnsPositiveValueAsync() {
    // Arrange
    var provider = new SystemTimeProvider();

    // Act
    var frequency = provider.TimestampFrequency;

    // Assert - Stopwatch.Frequency is typically in millions
    await Assert.That(frequency).IsGreaterThan(0);
  }

  [Test]
  public async Task TimestampFrequency_WithFakeTimeProvider_ReturnsFrequencyAsync() {
    // Arrange
    var fakeTime = new FakeTimeProvider();
    var provider = new SystemTimeProvider(fakeTime);

    // Act
    var frequency = provider.TimestampFrequency;

    // Assert
    await Assert.That(frequency).IsGreaterThan(0);
  }

  #endregion

  #region DI Registration Tests

  [Test]
  public async Task AddWhizbang_RegistersITimeProvider_AsSingletonAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddWhizbang();
    var serviceProvider = services.BuildServiceProvider();

    // Act
    var instance1 = serviceProvider.GetRequiredService<ITimeProvider>();
    var instance2 = serviceProvider.GetRequiredService<ITimeProvider>();

    // Assert
    await Assert.That(instance1).IsNotNull();
    await Assert.That(instance1).IsSameReferenceAs(instance2);
  }

  [Test]
  public async Task AddWhizbang_RegistersSystemTimeProvider_AsImplementationAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddWhizbang();
    var serviceProvider = services.BuildServiceProvider();

    // Act
    var instance = serviceProvider.GetRequiredService<ITimeProvider>();

    // Assert
    await Assert.That(instance).IsTypeOf<SystemTimeProvider>();
  }

  [Test]
  public async Task ITimeProvider_FromDI_ReturnsValidTimeAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddWhizbang();
    var serviceProvider = services.BuildServiceProvider();
    var timeProvider = serviceProvider.GetRequiredService<ITimeProvider>();

    // Act
    var now = timeProvider.GetUtcNow();

    // Assert - should be close to actual time
    var difference = (DateTimeOffset.UtcNow - now).Duration();
    await Assert.That(difference).IsLessThan(TimeSpan.FromSeconds(1));
  }

  #endregion
}
