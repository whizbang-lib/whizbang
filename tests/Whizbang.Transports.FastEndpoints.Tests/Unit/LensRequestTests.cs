using Whizbang.Transports.FastEndpoints;

namespace Whizbang.Transports.FastEndpoints.Tests.Unit;

/// <summary>
/// Tests for <see cref="LensRequest"/>.
/// Verifies request model defaults and property behavior.
/// </summary>
public class LensRequestTests {
  [Test]
  public async Task Constructor_ShouldSetDefaultsAsync() {
    // Arrange & Act
    var request = new LensRequest();

    // Assert - verify all defaults
    await Assert.That(request.Page).IsEqualTo(1);
    await Assert.That(request.PageSize).IsNull();
    await Assert.That(request.Sort).IsNull();
    await Assert.That(request.Filter).IsNull();
  }

  [Test]
  public async Task Page_ShouldBeSettableAsync() {
    // Arrange
    var request = new LensRequest();

    // Act
    request.Page = 5;

    // Assert
    await Assert.That(request.Page).IsEqualTo(5);
  }

  [Test]
  public async Task PageSize_ShouldBeSettableAsync() {
    // Arrange
    var request = new LensRequest();

    // Act
    request.PageSize = 25;

    // Assert
    await Assert.That(request.PageSize).IsEqualTo(25);
  }

  [Test]
  public async Task Sort_ShouldBeSettableAsync() {
    // Arrange
    var request = new LensRequest();

    // Act
    request.Sort = "-createdAt";

    // Assert
    await Assert.That(request.Sort).IsEqualTo("-createdAt");
  }

  [Test]
  public async Task Filter_ShouldBeSettableAsync() {
    // Arrange
    var request = new LensRequest();

    // Act
    request.Filter = new Dictionary<string, string> { ["name"] = "John" };

    // Assert
    await Assert.That(request.Filter).IsNotNull();
    await Assert.That(request.Filter!["name"]).IsEqualTo("John");
  }

  [Test]
  public async Task Filter_ShouldAllowMultipleFiltersAsync() {
    // Arrange
    var request = new LensRequest {
      Filter = new Dictionary<string, string> {
        ["name"] = "John",
        ["status"] = "active",
        ["age"] = "30"
      }
    };

    // Assert
    await Assert.That(request.Filter).Count().IsEqualTo(3);
    await Assert.That(request.Filter!["name"]).IsEqualTo("John");
    await Assert.That(request.Filter["status"]).IsEqualTo("active");
    await Assert.That(request.Filter["age"]).IsEqualTo("30");
  }

  [Test]
  public async Task Sort_WithMultipleFields_ShouldRetainOrderAsync() {
    // Arrange
    var request = new LensRequest();

    // Act - multiple sort fields in OData style
    request.Sort = "-createdAt,name,+status";

    // Assert
    await Assert.That(request.Sort).IsEqualTo("-createdAt,name,+status");
  }

  [Test]
  public async Task Page_Zero_ShouldBeAllowedAsync() {
    // Arrange - validation is done at runtime, not in the model
    var request = new LensRequest();

    // Act
    request.Page = 0;

    // Assert
    await Assert.That(request.Page).IsEqualTo(0);
  }

  [Test]
  public async Task PageSize_Zero_ShouldBeAllowedAsync() {
    // Arrange
    var request = new LensRequest();

    // Act
    request.PageSize = 0;

    // Assert
    await Assert.That(request.PageSize).IsEqualTo(0);
  }

  [Test]
  public async Task AllPropertiesSet_ShouldRetainValuesAsync() {
    // Arrange & Act
    var request = new LensRequest {
      Page = 3,
      PageSize = 50,
      Sort = "-updatedAt",
      Filter = new Dictionary<string, string> { ["category"] = "electronics" }
    };

    // Assert
    await Assert.That(request.Page).IsEqualTo(3);
    await Assert.That(request.PageSize).IsEqualTo(50);
    await Assert.That(request.Sort).IsEqualTo("-updatedAt");
    await Assert.That(request.Filter!["category"]).IsEqualTo("electronics");
  }
}
