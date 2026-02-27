using System.Linq.Expressions;
using Whizbang.Transports.HotChocolate.QueryTranslation;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// Tests for <see cref="OrderByStrippingExpressionVisitor"/>.
/// Verifies that ordering expressions are correctly stripped from IQueryable expressions.
/// </summary>
public class OrderByStrippingExpressionVisitorTests {
  private readonly OrderByStrippingExpressionVisitor _visitor = new();

  #region StripOrderBy Tests

  [Test]
  public async Task StripOrderBy_RemovesSimpleOrderByAsync() {
    // Arrange
    var source = new List<TestItem> { new(1, "A"), new(2, "B") }.AsQueryable();
    var orderedQuery = source.OrderBy(x => x.Id);
    var originalExpression = orderedQuery.Expression;

    // Act
    var strippedExpression = _visitor.Visit(originalExpression);

    // Assert - The resulting expression should not contain OrderBy
    await Assert.That(_containsOrderingMethod(strippedExpression)).IsFalse();
  }

  [Test]
  public async Task StripOrderBy_RemovesOrderByDescendingAsync() {
    // Arrange
    var source = new List<TestItem> { new(1, "A"), new(2, "B") }.AsQueryable();
    var orderedQuery = source.OrderByDescending(x => x.Id);
    var originalExpression = orderedQuery.Expression;

    // Act
    var strippedExpression = _visitor.Visit(originalExpression);

    // Assert
    await Assert.That(_containsOrderingMethod(strippedExpression)).IsFalse();
  }

  [Test]
  public async Task StripOrderBy_RemovesChainedThenByAsync() {
    // Arrange
    var source = new List<TestItem> { new(1, "A"), new(2, "B") }.AsQueryable();
    var orderedQuery = source.OrderBy(x => x.Id).ThenBy(x => x.Name);
    var originalExpression = orderedQuery.Expression;

    // Act
    var strippedExpression = _visitor.Visit(originalExpression);

    // Assert
    await Assert.That(_containsOrderingMethod(strippedExpression)).IsFalse();
  }

  [Test]
  public async Task StripOrderBy_RemovesChainedThenByDescendingAsync() {
    // Arrange
    var source = new List<TestItem> { new(1, "A"), new(2, "B") }.AsQueryable();
    var orderedQuery = source.OrderBy(x => x.Id).ThenByDescending(x => x.Name);
    var originalExpression = orderedQuery.Expression;

    // Act
    var strippedExpression = _visitor.Visit(originalExpression);

    // Assert
    await Assert.That(_containsOrderingMethod(strippedExpression)).IsFalse();
  }

  [Test]
  public async Task StripOrderBy_RemovesComplexChainedOrderingAsync() {
    // Arrange - Multiple ThenBy/ThenByDescending
    var source = new List<TestItem> { new(1, "A"), new(2, "B") }.AsQueryable();
    var orderedQuery = source
        .OrderBy(x => x.Id)
        .ThenByDescending(x => x.Name)
        .ThenBy(x => x.Id);
    var originalExpression = orderedQuery.Expression;

    // Act
    var strippedExpression = _visitor.Visit(originalExpression);

    // Assert
    await Assert.That(_containsOrderingMethod(strippedExpression)).IsFalse();
  }

  #endregion

  #region Preservation Tests

  [Test]
  public async Task StripOrderBy_PreservesWhereClauseAsync() {
    // Arrange
    var source = new List<TestItem> { new(1, "A"), new(2, "B") }.AsQueryable();
    var query = source.Where(x => x.Id > 0).OrderBy(x => x.Name);
    var originalExpression = query.Expression;

    // Act
    var strippedExpression = _visitor.Visit(originalExpression);

    // Assert - Where should be preserved
    await Assert.That(_containsMethodCall(strippedExpression, "Where")).IsTrue();
    await Assert.That(_containsOrderingMethod(strippedExpression)).IsFalse();
  }

  [Test]
  public async Task StripOrderBy_PreservesSelectClauseAsync() {
    // Arrange
    var source = new List<TestItem> { new(1, "A"), new(2, "B") }.AsQueryable();
    var query = source.Select(x => x.Name).OrderBy(x => x);
    var originalExpression = query.Expression;

    // Act
    var strippedExpression = _visitor.Visit(originalExpression);

    // Assert - Select should be preserved
    await Assert.That(_containsMethodCall(strippedExpression, "Select")).IsTrue();
    await Assert.That(_containsOrderingMethod(strippedExpression)).IsFalse();
  }

  [Test]
  public async Task StripOrderBy_HandlesNestedOrderByAfterWhereAsync() {
    // Arrange - OrderBy nested after Where
    var source = new List<TestItem> { new(1, "A"), new(2, "B") }.AsQueryable();
    var query = source.Where(x => x.Id > 0).OrderBy(x => x.Name).Where(x => x.Name != null);
    var originalExpression = query.Expression;

    // Act
    var strippedExpression = _visitor.Visit(originalExpression);

    // Assert - Both Where clauses preserved, OrderBy removed
    await Assert.That(_containsOrderingMethod(strippedExpression)).IsFalse();
    // The expression should still be valid and contain Where
    await Assert.That(_containsMethodCall(strippedExpression, "Where")).IsTrue();
  }

  [Test]
  public async Task StripOrderBy_ReturnsUnmodifiedWhenNoOrderingAsync() {
    // Arrange - No ordering present
    var source = new List<TestItem> { new(1, "A"), new(2, "B") }.AsQueryable();
    var query = source.Where(x => x.Id > 0);
    var originalExpression = query.Expression;

    // Act
    var strippedExpression = _visitor.Visit(originalExpression);

    // Assert - Expression should remain functionally equivalent
    await Assert.That(_containsMethodCall(strippedExpression, "Where")).IsTrue();
    await Assert.That(_containsOrderingMethod(strippedExpression)).IsFalse();
  }

  #endregion

  #region Edge Cases

  [Test]
  public async Task StripOrderBy_HandlesEmptyQueryableAsync() {
    // Arrange
    var source = new List<TestItem>().AsQueryable();
    var query = source.OrderBy(x => x.Id);
    var originalExpression = query.Expression;

    // Act
    var strippedExpression = _visitor.Visit(originalExpression);

    // Assert
    await Assert.That(_containsOrderingMethod(strippedExpression)).IsFalse();
  }

  [Test]
  public async Task StripOrderBy_PreservesSkipAndTakeAsync() {
    // Arrange
    var source = new List<TestItem> { new(1, "A"), new(2, "B") }.AsQueryable();
    var query = source.OrderBy(x => x.Id).Skip(1).Take(1);
    var originalExpression = query.Expression;

    // Act
    var strippedExpression = _visitor.Visit(originalExpression);

    // Assert - Skip and Take preserved, OrderBy removed
    await Assert.That(_containsMethodCall(strippedExpression, "Skip")).IsTrue();
    await Assert.That(_containsMethodCall(strippedExpression, "Take")).IsTrue();
    await Assert.That(_containsOrderingMethod(strippedExpression)).IsFalse();
  }

  [Test]
  public async Task StripOrderBy_PreservesDistinctAsync() {
    // Arrange
    var source = new List<TestItem> { new(1, "A"), new(2, "B") }.AsQueryable();
    var query = source.Distinct().OrderBy(x => x.Id);
    var originalExpression = query.Expression;

    // Act
    var strippedExpression = _visitor.Visit(originalExpression);

    // Assert - Distinct preserved, OrderBy removed
    await Assert.That(_containsMethodCall(strippedExpression, "Distinct")).IsTrue();
    await Assert.That(_containsOrderingMethod(strippedExpression)).IsFalse();
  }

  #endregion

  #region Functional Verification

  [Test]
  public async Task StripOrderBy_ResultingExpressionIsExecutableAsync() {
    // Arrange
    var source = new List<TestItem> { new(1, "A"), new(2, "B") }.AsQueryable();
    var orderedQuery = source.Where(x => x.Id > 0).OrderBy(x => x.Name);
    var originalExpression = orderedQuery.Expression;

    // Act
    var strippedExpression = _visitor.Visit(originalExpression);

    // Assert - Create a new queryable from the stripped expression and execute it
    var strippedQuery = source.Provider.CreateQuery<TestItem>(strippedExpression);
    var results = strippedQuery.ToList();
    await Assert.That(results.Count).IsEqualTo(2);
  }

  #endregion

  #region Helper Methods

  private static bool _containsOrderingMethod(Expression expression) {
    return _containsMethodCall(expression, "OrderBy") ||
           _containsMethodCall(expression, "OrderByDescending") ||
           _containsMethodCall(expression, "ThenBy") ||
           _containsMethodCall(expression, "ThenByDescending");
  }

  private static bool _containsMethodCall(Expression expression, string methodName) {
    var finder = new MethodCallFinder(methodName);
    finder.Visit(expression);
    return finder.Found;
  }

  private sealed class MethodCallFinder(string methodName) : ExpressionVisitor {
    public bool Found { get; private set; }

    protected override Expression VisitMethodCall(MethodCallExpression node) {
      if (node.Method.Name == methodName) {
        Found = true;
      }

      return base.VisitMethodCall(node);
    }
  }

  #endregion

  #region Test Data

  private sealed record TestItem(int Id, string Name);

  #endregion
}
