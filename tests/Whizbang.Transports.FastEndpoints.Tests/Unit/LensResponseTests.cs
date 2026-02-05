using Whizbang.Transports.FastEndpoints;

namespace Whizbang.Transports.FastEndpoints.Tests.Unit;

/// <summary>
/// Tests for <see cref="LensResponse{TModel}"/>.
/// Verifies response model defaults and paging calculation.
/// </summary>
public class LensResponseTests {
  [Test]
  public async Task Constructor_ShouldSetDefaultsAsync() {
    // Arrange & Act
    var response = new LensResponse<TestModel>();

    // Assert
    await Assert.That(response.Data).IsNotNull();
    await Assert.That(response.Data).Count().IsEqualTo(0);
    await Assert.That(response.TotalCount).IsEqualTo(0);
    await Assert.That(response.Page).IsEqualTo(1);
    await Assert.That(response.PageSize).IsEqualTo(10);
  }

  [Test]
  public async Task Data_ShouldBeSettableAsync() {
    // Arrange
    var items = new List<TestModel> {
      new() { Id = Guid.NewGuid(), Name = "Item 1" },
      new() { Id = Guid.NewGuid(), Name = "Item 2" }
    };
    var response = new LensResponse<TestModel>();

    // Act
    response.Data = items;

    // Assert
    await Assert.That(response.Data).Count().IsEqualTo(2);
  }

  [Test]
  public async Task TotalCount_ShouldBeSettableAsync() {
    // Arrange
    var response = new LensResponse<TestModel>();

    // Act
    response.TotalCount = 100;

    // Assert
    await Assert.That(response.TotalCount).IsEqualTo(100);
  }

  [Test]
  public async Task Page_ShouldBeSettableAsync() {
    // Arrange
    var response = new LensResponse<TestModel>();

    // Act
    response.Page = 5;

    // Assert
    await Assert.That(response.Page).IsEqualTo(5);
  }

  [Test]
  public async Task PageSize_ShouldBeSettableAsync() {
    // Arrange
    var response = new LensResponse<TestModel>();

    // Act
    response.PageSize = 25;

    // Assert
    await Assert.That(response.PageSize).IsEqualTo(25);
  }

  [Test]
  public async Task TotalPages_ShouldCalculateCorrectlyAsync() {
    // Arrange & Act
    var response = new LensResponse<TestModel> {
      TotalCount = 95,
      PageSize = 10
    };

    // Assert - 95 items / 10 per page = 10 pages (ceiling)
    await Assert.That(response.TotalPages).IsEqualTo(10);
  }

  [Test]
  public async Task TotalPages_WithExactDivision_ShouldCalculateCorrectlyAsync() {
    // Arrange & Act
    var response = new LensResponse<TestModel> {
      TotalCount = 100,
      PageSize = 10
    };

    // Assert - 100 items / 10 per page = 10 pages
    await Assert.That(response.TotalPages).IsEqualTo(10);
  }

  [Test]
  public async Task TotalPages_WithZeroItems_ShouldBeZeroAsync() {
    // Arrange & Act
    var response = new LensResponse<TestModel> {
      TotalCount = 0,
      PageSize = 10
    };

    // Assert
    await Assert.That(response.TotalPages).IsEqualTo(0);
  }

  [Test]
  public async Task TotalPages_WithZeroPageSize_ShouldBeZeroAsync() {
    // Arrange & Act - edge case protection
    var response = new LensResponse<TestModel> {
      TotalCount = 100,
      PageSize = 0
    };

    // Assert - should not throw, returns 0
    await Assert.That(response.TotalPages).IsEqualTo(0);
  }

  [Test]
  public async Task HasNextPage_ShouldBeTrueWhenMorePagesExistAsync() {
    // Arrange & Act
    var response = new LensResponse<TestModel> {
      TotalCount = 100,
      PageSize = 10,
      Page = 5
    };

    // Assert - page 5 of 10, more pages exist
    await Assert.That(response.HasNextPage).IsTrue();
  }

  [Test]
  public async Task HasNextPage_ShouldBeFalseOnLastPageAsync() {
    // Arrange & Act
    var response = new LensResponse<TestModel> {
      TotalCount = 100,
      PageSize = 10,
      Page = 10
    };

    // Assert - page 10 of 10, no more pages
    await Assert.That(response.HasNextPage).IsFalse();
  }

  [Test]
  public async Task HasPreviousPage_ShouldBeTrueWhenNotOnFirstPageAsync() {
    // Arrange & Act
    var response = new LensResponse<TestModel> {
      Page = 5
    };

    // Assert
    await Assert.That(response.HasPreviousPage).IsTrue();
  }

  [Test]
  public async Task HasPreviousPage_ShouldBeFalseOnFirstPageAsync() {
    // Arrange & Act
    var response = new LensResponse<TestModel> {
      Page = 1
    };

    // Assert
    await Assert.That(response.HasPreviousPage).IsFalse();
  }

  [Test]
  public async Task AllPropertiesSet_ShouldRetainValuesAsync() {
    // Arrange
    var items = new List<TestModel> {
      new() { Id = Guid.NewGuid(), Name = "Product A" },
      new() { Id = Guid.NewGuid(), Name = "Product B" },
      new() { Id = Guid.NewGuid(), Name = "Product C" }
    };

    // Act
    var response = new LensResponse<TestModel> {
      Data = items,
      TotalCount = 50,
      Page = 2,
      PageSize = 3
    };

    // Assert
    await Assert.That(response.Data).Count().IsEqualTo(3);
    await Assert.That(response.TotalCount).IsEqualTo(50);
    await Assert.That(response.Page).IsEqualTo(2);
    await Assert.That(response.PageSize).IsEqualTo(3);
    await Assert.That(response.TotalPages).IsEqualTo(17); // ceiling(50/3) = 17
    await Assert.That(response.HasNextPage).IsTrue();
    await Assert.That(response.HasPreviousPage).IsTrue();
  }

  [Test]
  public async Task Response_WithSinglePageOfData_ShouldHaveCorrectPagingAsync() {
    // Arrange & Act
    var response = new LensResponse<TestModel> {
      Data = [new() { Id = Guid.NewGuid(), Name = "Only Item" }],
      TotalCount = 1,
      Page = 1,
      PageSize = 10
    };

    // Assert
    await Assert.That(response.TotalPages).IsEqualTo(1);
    await Assert.That(response.HasNextPage).IsFalse();
    await Assert.That(response.HasPreviousPage).IsFalse();
  }
}

/// <summary>
/// Test model for LensResponse tests.
/// </summary>
public class TestModel {
  public Guid Id { get; set; }
  public string Name { get; set; } = string.Empty;
}
