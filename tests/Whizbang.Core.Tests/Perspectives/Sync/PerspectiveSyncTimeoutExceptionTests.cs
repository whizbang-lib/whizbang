using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Tests for <see cref="PerspectiveSyncTimeoutException"/>.
/// </summary>
public class PerspectiveSyncTimeoutExceptionTests {
  [Test]
  public async Task DefaultConstructor_CreatesExceptionAsync() {
    // Act
    var ex = new PerspectiveSyncTimeoutException();

    // Assert
    await Assert.That(ex).IsNotNull();
    await Assert.That(ex.PerspectiveType).IsNull();
    await Assert.That(ex.Timeout).IsEqualTo(TimeSpan.Zero);
  }

  [Test]
  public async Task MessageConstructor_SetsMessageAsync() {
    // Arrange
    var message = "Sync timed out";

    // Act
    var ex = new PerspectiveSyncTimeoutException(message);

    // Assert
    await Assert.That(ex.Message).IsEqualTo(message);
    await Assert.That(ex.PerspectiveType).IsNull();
    await Assert.That(ex.Timeout).IsEqualTo(TimeSpan.Zero);
  }

  [Test]
  public async Task MessageAndInnerConstructor_SetsBothAsync() {
    // Arrange
    var message = "Sync timed out";
    var inner = new InvalidOperationException("Inner error");

    // Act
    var ex = new PerspectiveSyncTimeoutException(message, inner);

    // Assert
    await Assert.That(ex.Message).IsEqualTo(message);
    await Assert.That(ex.InnerException).IsSameReferenceAs(inner);
    await Assert.That(ex.PerspectiveType).IsNull();
    await Assert.That(ex.Timeout).IsEqualTo(TimeSpan.Zero);
  }

  [Test]
  public async Task TypeAndTimeoutConstructor_SetsAllPropertiesAsync() {
    // Arrange
    var perspectiveType = typeof(object);
    var timeout = TimeSpan.FromSeconds(30);
    var message = "Perspective sync timed out after 30 seconds";

    // Act
    var ex = new PerspectiveSyncTimeoutException(perspectiveType, timeout, message);

    // Assert
    await Assert.That(ex.Message).IsEqualTo(message);
    await Assert.That(ex.PerspectiveType).IsEqualTo(perspectiveType);
    await Assert.That(ex.Timeout).IsEqualTo(timeout);
    await Assert.That(ex.InnerException).IsNull();
  }

  [Test]
  public async Task TypeTimeoutAndInnerConstructor_SetsAllPropertiesAsync() {
    // Arrange
    var perspectiveType = typeof(string);
    var timeout = TimeSpan.FromMinutes(1);
    var message = "Perspective sync failed";
    var inner = new TimeoutException("Database timeout");

    // Act
    var ex = new PerspectiveSyncTimeoutException(perspectiveType, timeout, message, inner);

    // Assert
    await Assert.That(ex.Message).IsEqualTo(message);
    await Assert.That(ex.PerspectiveType).IsEqualTo(perspectiveType);
    await Assert.That(ex.Timeout).IsEqualTo(timeout);
    await Assert.That(ex.InnerException).IsSameReferenceAs(inner);
  }

  [Test]
  public async Task Exception_InheritsFromSystemExceptionAsync() {
    // Assert
    await Assert.That(typeof(PerspectiveSyncTimeoutException).IsSubclassOf(typeof(Exception))).IsTrue();
  }

  [Test]
  public async Task Exception_CanBeThrownAndCaughtAsync() {
    // Arrange
    var perspectiveType = typeof(int);
    var timeout = TimeSpan.FromSeconds(5);
    var message = "Test timeout";

    // Act & Assert
    await Assert.That(() => {
      throw new PerspectiveSyncTimeoutException(perspectiveType, timeout, message);
    }).ThrowsExactly<PerspectiveSyncTimeoutException>();
  }

  [Test]
  public async Task Exception_PreservesStackTraceAsync() {
    // Arrange
    PerspectiveSyncTimeoutException? caught = null;

    // Act
    try {
      throw new PerspectiveSyncTimeoutException("Test");
    } catch (PerspectiveSyncTimeoutException ex) {
      caught = ex;
    }

    // Assert
    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.StackTrace).IsNotNull();
  }
}
