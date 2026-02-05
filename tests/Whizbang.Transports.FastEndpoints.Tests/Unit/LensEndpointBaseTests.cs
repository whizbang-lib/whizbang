using Whizbang.Core.Lenses;
using Whizbang.Transports.FastEndpoints;

namespace Whizbang.Transports.FastEndpoints.Tests.Unit;

/// <summary>
/// Tests for <see cref="LensEndpointBase{TModel}"/>.
/// Verifies query building, paging, and hook execution.
/// </summary>
public class LensEndpointBaseTests {
  [Test]
  public async Task ApplyPaging_ShouldCalculateSkipAndTakeCorrectlyAsync() {
    // Arrange
    var endpoint = new TestLensEndpoint();
    var request = new LensRequest { Page = 3, PageSize = 10 };

    // Act
    var (skip, take) = endpoint.TestCalculatePaging(request, defaultPageSize: 10, maxPageSize: 100);

    // Assert - page 3 with 10 items = skip 20
    await Assert.That(skip).IsEqualTo(20);
    await Assert.That(take).IsEqualTo(10);
  }

  [Test]
  public async Task ApplyPaging_WithFirstPage_ShouldSkipZeroAsync() {
    // Arrange
    var endpoint = new TestLensEndpoint();
    var request = new LensRequest { Page = 1, PageSize = 25 };

    // Act
    var (skip, take) = endpoint.TestCalculatePaging(request, defaultPageSize: 10, maxPageSize: 100);

    // Assert
    await Assert.That(skip).IsEqualTo(0);
    await Assert.That(take).IsEqualTo(25);
  }

  [Test]
  public async Task ApplyPaging_WithNullPageSize_ShouldUseDefaultAsync() {
    // Arrange
    var endpoint = new TestLensEndpoint();
    var request = new LensRequest { Page = 1, PageSize = null };

    // Act
    var (skip, take) = endpoint.TestCalculatePaging(request, defaultPageSize: 15, maxPageSize: 100);

    // Assert
    await Assert.That(take).IsEqualTo(15);
  }

  [Test]
  public async Task ApplyPaging_WithExcessivePageSize_ShouldClampToMaxAsync() {
    // Arrange
    var endpoint = new TestLensEndpoint();
    var request = new LensRequest { Page = 1, PageSize = 500 };

    // Act
    var (skip, take) = endpoint.TestCalculatePaging(request, defaultPageSize: 10, maxPageSize: 100);

    // Assert - should be clamped to max 100
    await Assert.That(take).IsEqualTo(100);
  }

  [Test]
  public async Task ApplyPaging_WithZeroPage_ShouldTreatAsFirstPageAsync() {
    // Arrange
    var endpoint = new TestLensEndpoint();
    var request = new LensRequest { Page = 0, PageSize = 10 };

    // Act
    var (skip, take) = endpoint.TestCalculatePaging(request, defaultPageSize: 10, maxPageSize: 100);

    // Assert - page 0 should be treated as page 1
    await Assert.That(skip).IsEqualTo(0);
  }

  [Test]
  public async Task ApplyPaging_WithNegativePage_ShouldTreatAsFirstPageAsync() {
    // Arrange
    var endpoint = new TestLensEndpoint();
    var request = new LensRequest { Page = -5, PageSize = 10 };

    // Act
    var (skip, take) = endpoint.TestCalculatePaging(request, defaultPageSize: 10, maxPageSize: 100);

    // Assert
    await Assert.That(skip).IsEqualTo(0);
  }

  [Test]
  public async Task ParseSortExpression_WithDescendingPrefix_ShouldParseCorrectlyAsync() {
    // Arrange
    var endpoint = new TestLensEndpoint();

    // Act
    var sorts = endpoint.TestParseSortExpression("-createdAt");

    // Assert
    await Assert.That(sorts).Count().IsEqualTo(1);
    await Assert.That(sorts[0].Field).IsEqualTo("createdAt");
    await Assert.That(sorts[0].Descending).IsTrue();
  }

  [Test]
  public async Task ParseSortExpression_WithAscendingPrefix_ShouldParseCorrectlyAsync() {
    // Arrange
    var endpoint = new TestLensEndpoint();

    // Act
    var sorts = endpoint.TestParseSortExpression("+name");

    // Assert
    await Assert.That(sorts).Count().IsEqualTo(1);
    await Assert.That(sorts[0].Field).IsEqualTo("name");
    await Assert.That(sorts[0].Descending).IsFalse();
  }

  [Test]
  public async Task ParseSortExpression_WithNoPrefix_ShouldDefaultToAscendingAsync() {
    // Arrange
    var endpoint = new TestLensEndpoint();

    // Act
    var sorts = endpoint.TestParseSortExpression("status");

    // Assert
    await Assert.That(sorts[0].Field).IsEqualTo("status");
    await Assert.That(sorts[0].Descending).IsFalse();
  }

  [Test]
  public async Task ParseSortExpression_WithMultipleFields_ShouldParseAllAsync() {
    // Arrange
    var endpoint = new TestLensEndpoint();

    // Act
    var sorts = endpoint.TestParseSortExpression("-priority,createdAt,+name");

    // Assert
    await Assert.That(sorts).Count().IsEqualTo(3);
    await Assert.That(sorts[0].Field).IsEqualTo("priority");
    await Assert.That(sorts[0].Descending).IsTrue();
    await Assert.That(sorts[1].Field).IsEqualTo("createdAt");
    await Assert.That(sorts[1].Descending).IsFalse();
    await Assert.That(sorts[2].Field).IsEqualTo("name");
    await Assert.That(sorts[2].Descending).IsFalse();
  }

  [Test]
  public async Task ParseSortExpression_WithNullInput_ShouldReturnEmptyAsync() {
    // Arrange
    var endpoint = new TestLensEndpoint();

    // Act
    var sorts = endpoint.TestParseSortExpression(null);

    // Assert
    await Assert.That(sorts).Count().IsEqualTo(0);
  }

  [Test]
  public async Task ParseSortExpression_WithEmptyInput_ShouldReturnEmptyAsync() {
    // Arrange
    var endpoint = new TestLensEndpoint();

    // Act
    var sorts = endpoint.TestParseSortExpression("");

    // Assert
    await Assert.That(sorts).Count().IsEqualTo(0);
  }

  [Test]
  public async Task ParseSortExpression_WithWhitespace_ShouldTrimFieldsAsync() {
    // Arrange
    var endpoint = new TestLensEndpoint();

    // Act
    var sorts = endpoint.TestParseSortExpression(" -name , +status ");

    // Assert
    await Assert.That(sorts).Count().IsEqualTo(2);
    await Assert.That(sorts[0].Field).IsEqualTo("name");
    await Assert.That(sorts[1].Field).IsEqualTo("status");
  }

  [Test]
  public async Task OnBeforeQueryAsync_Default_ShouldCompleteImmediatelyAsync() {
    // Arrange
    var endpoint = new TestLensEndpoint();
    var request = new LensRequest();
    var ct = CancellationToken.None;

    // Act & Assert - should not throw
    await endpoint.TestOnBeforeQueryAsync(request, ct);
  }

  [Test]
  public async Task OnAfterQueryAsync_Default_ShouldCompleteImmediatelyAsync() {
    // Arrange
    var endpoint = new TestLensEndpoint();
    var request = new LensRequest();
    var response = new LensResponse<TestReadModel>();
    var ct = CancellationToken.None;

    // Act & Assert - should not throw
    await endpoint.TestOnAfterQueryAsync(request, response, ct);
  }

  [Test]
  public async Task Endpoint_ShouldBeAbstractAsync() {
    // Assert
    var type = typeof(LensEndpointBase<TestReadModel>);
    await Assert.That(type.IsAbstract).IsTrue();
  }
}

/// <summary>
/// Test read model for endpoint tests.
/// </summary>
public class TestReadModel {
  public Guid Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public DateTime CreatedAt { get; set; }
  public int Priority { get; set; }
}

/// <summary>
/// Test implementation of LensEndpointBase for testing.
/// </summary>
public class TestLensEndpoint : LensEndpointBase<TestReadModel> {
  // Expose protected methods for testing
  public (int skip, int take) TestCalculatePaging(LensRequest request, int defaultPageSize, int maxPageSize)
      => CalculatePaging(request, defaultPageSize, maxPageSize);

  public IReadOnlyList<SortExpression> TestParseSortExpression(string? sort)
      => ParseSortExpression(sort);

  public ValueTask TestOnBeforeQueryAsync(LensRequest request, CancellationToken ct)
      => OnBeforeQueryAsync(request, ct);

  public ValueTask TestOnAfterQueryAsync(LensRequest request, LensResponse<TestReadModel> response, CancellationToken ct)
      => OnAfterQueryAsync(request, response, ct);
}
