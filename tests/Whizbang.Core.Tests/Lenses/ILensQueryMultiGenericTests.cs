using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;

namespace Whizbang.Core.Tests.Lenses;

/// <summary>
/// Tests for multi-generic ILensQuery interfaces (ILensQuery&lt;T1, T2&gt; through ILensQuery&lt;T1, ..., T10&gt;).
/// Verifies interface contracts, inheritance hierarchy, and method signatures.
/// </summary>
[Category("Core")]
[Category("Lenses")]
[Category("Unit")]
public class ILensQueryMultiGenericTests {
  // Test models
  private sealed record Model1 {
    public required Guid Id { get; init; }
    public required string Name { get; init; }
  }

  private sealed record Model2 {
    public required Guid Id { get; init; }
    public required int Value { get; init; }
  }

  private sealed record Model3 {
    public required Guid Id { get; init; }
    public required decimal Amount { get; init; }
  }

  private sealed record Model4 {
    public required Guid Id { get; init; }
    public required DateTime CreatedAt { get; init; }
  }

  private sealed record Model5 {
    public required Guid Id { get; init; }
    public required bool IsActive { get; init; }
  }

  #region Two Generic Parameters - ILensQuery<T1, T2>

  [Test]
  public async Task ILensQuery_TwoGeneric_ImplementsILensQueryAsync() {
    // Arrange
    var type = typeof(ILensQuery<Model1, Model2>);

    // Act
    var implementsILensQuery = typeof(ILensQuery).IsAssignableFrom(type);

    // Assert
    await Assert.That(implementsILensQuery).IsTrue();
  }

  [Test]
  public async Task ILensQuery_TwoGeneric_ImplementsIAsyncDisposableAsync() {
    // Arrange
    var type = typeof(ILensQuery<Model1, Model2>);

    // Act
    var implementsIAsyncDisposable = typeof(IAsyncDisposable).IsAssignableFrom(type);

    // Assert
    await Assert.That(implementsIAsyncDisposable).IsTrue();
  }

  [Test]
  public async Task ILensQuery_TwoGeneric_HasQueryMethodAsync() {
    // Arrange
    var type = typeof(ILensQuery<Model1, Model2>);

    // Act
    var queryMethod = type.GetMethod("Query");

    // Assert
    await Assert.That(queryMethod).IsNotNull();
    await Assert.That(queryMethod!.IsGenericMethod).IsTrue();
  }

  [Test]
  public async Task ILensQuery_TwoGeneric_HasGetByIdAsyncMethodAsync() {
    // Arrange
    var type = typeof(ILensQuery<Model1, Model2>);

    // Act
    var getByIdMethod = type.GetMethod("GetByIdAsync");

    // Assert
    await Assert.That(getByIdMethod).IsNotNull();
    await Assert.That(getByIdMethod!.IsGenericMethod).IsTrue();
  }

  [Test]
  public async Task ILensQuery_TwoGeneric_QueryMethod_ReturnsIQueryableAsync() {
    // Arrange
    var type = typeof(ILensQuery<Model1, Model2>);
    var queryMethod = type.GetMethod("Query")!;
    var genericMethod = queryMethod.MakeGenericMethod(typeof(Model1));

    // Act
    var returnType = genericMethod.ReturnType;

    // Assert
    await Assert.That(returnType.IsGenericType).IsTrue();
    await Assert.That(returnType.GetGenericTypeDefinition()).IsEqualTo(typeof(IQueryable<>));
  }

  [Test]
  public async Task ILensQuery_TwoGeneric_GetByIdAsync_ReturnsTaskAsync() {
    // Arrange
    var type = typeof(ILensQuery<Model1, Model2>);
    var getByIdMethod = type.GetMethod("GetByIdAsync")!;
    var genericMethod = getByIdMethod.MakeGenericMethod(typeof(Model1));

    // Act
    var returnType = genericMethod.ReturnType;

    // Assert
    await Assert.That(returnType.IsGenericType).IsTrue();
    await Assert.That(returnType.GetGenericTypeDefinition()).IsEqualTo(typeof(Task<>));
  }

  #endregion

  #region Three Generic Parameters - ILensQuery<T1, T2, T3>

  [Test]
  public async Task ILensQuery_ThreeGeneric_ImplementsILensQueryAsync() {
    // Arrange
    var type = typeof(ILensQuery<Model1, Model2, Model3>);

    // Act
    var implementsILensQuery = typeof(ILensQuery).IsAssignableFrom(type);

    // Assert
    await Assert.That(implementsILensQuery).IsTrue();
  }

  [Test]
  public async Task ILensQuery_ThreeGeneric_ImplementsIAsyncDisposableAsync() {
    // Arrange
    var type = typeof(ILensQuery<Model1, Model2, Model3>);

    // Act
    var implementsIAsyncDisposable = typeof(IAsyncDisposable).IsAssignableFrom(type);

    // Assert
    await Assert.That(implementsIAsyncDisposable).IsTrue();
  }

  [Test]
  public async Task ILensQuery_ThreeGeneric_HasQueryMethodAsync() {
    // Arrange
    var type = typeof(ILensQuery<Model1, Model2, Model3>);

    // Act
    var queryMethod = type.GetMethod("Query");

    // Assert
    await Assert.That(queryMethod).IsNotNull();
    await Assert.That(queryMethod!.IsGenericMethod).IsTrue();
  }

  [Test]
  public async Task ILensQuery_ThreeGeneric_HasGetByIdAsyncMethodAsync() {
    // Arrange
    var type = typeof(ILensQuery<Model1, Model2, Model3>);

    // Act
    var getByIdMethod = type.GetMethod("GetByIdAsync");

    // Assert
    await Assert.That(getByIdMethod).IsNotNull();
    await Assert.That(getByIdMethod!.IsGenericMethod).IsTrue();
  }

  #endregion

  #region Four Generic Parameters - ILensQuery<T1, T2, T3, T4>

  [Test]
  public async Task ILensQuery_FourGeneric_ImplementsILensQueryAsync() {
    // Arrange
    var type = typeof(ILensQuery<Model1, Model2, Model3, Model4>);

    // Act
    var implementsILensQuery = typeof(ILensQuery).IsAssignableFrom(type);

    // Assert
    await Assert.That(implementsILensQuery).IsTrue();
  }

  [Test]
  public async Task ILensQuery_FourGeneric_ImplementsIAsyncDisposableAsync() {
    // Arrange
    var type = typeof(ILensQuery<Model1, Model2, Model3, Model4>);

    // Act
    var implementsIAsyncDisposable = typeof(IAsyncDisposable).IsAssignableFrom(type);

    // Assert
    await Assert.That(implementsIAsyncDisposable).IsTrue();
  }

  #endregion

  #region Five Generic Parameters - ILensQuery<T1, T2, T3, T4, T5>

  [Test]
  public async Task ILensQuery_FiveGeneric_ImplementsILensQueryAsync() {
    // Arrange
    var type = typeof(ILensQuery<Model1, Model2, Model3, Model4, Model5>);

    // Act
    var implementsILensQuery = typeof(ILensQuery).IsAssignableFrom(type);

    // Assert
    await Assert.That(implementsILensQuery).IsTrue();
  }

  [Test]
  public async Task ILensQuery_FiveGeneric_ImplementsIAsyncDisposableAsync() {
    // Arrange
    var type = typeof(ILensQuery<Model1, Model2, Model3, Model4, Model5>);

    // Act
    var implementsIAsyncDisposable = typeof(IAsyncDisposable).IsAssignableFrom(type);

    // Assert
    await Assert.That(implementsIAsyncDisposable).IsTrue();
  }

  #endregion

  #region Interface Definition Tests (Verify Generic Constraints)

  [Test]
  public async Task ILensQuery_TwoGeneric_HasClassConstraint_OnT1Async() {
    // Arrange
    var type = typeof(ILensQuery<,>);
    var genericArgs = type.GetGenericArguments();

    // Act
    var t1Constraints = genericArgs[0].GetGenericParameterConstraints();
    var t1Attributes = genericArgs[0].GenericParameterAttributes;

    // Assert - T1 has class constraint (reference type)
    await Assert.That(t1Attributes.HasFlag(
        System.Reflection.GenericParameterAttributes.ReferenceTypeConstraint)).IsTrue();
  }

  [Test]
  public async Task ILensQuery_TwoGeneric_HasClassConstraint_OnT2Async() {
    // Arrange
    var type = typeof(ILensQuery<,>);
    var genericArgs = type.GetGenericArguments();

    // Act
    var t2Constraints = genericArgs[1].GetGenericParameterConstraints();
    var t2Attributes = genericArgs[1].GenericParameterAttributes;

    // Assert - T2 has class constraint (reference type)
    await Assert.That(t2Attributes.HasFlag(
        System.Reflection.GenericParameterAttributes.ReferenceTypeConstraint)).IsTrue();
  }

  [Test]
  public async Task ILensQuery_ThreeGeneric_HasClassConstraint_OnAllParametersAsync() {
    // Arrange
    var type = typeof(ILensQuery<,,>);
    var genericArgs = type.GetGenericArguments();

    // Assert - All type parameters have class constraint
    foreach (var arg in genericArgs) {
      var attributes = arg.GenericParameterAttributes;
      await Assert.That(attributes.HasFlag(
          System.Reflection.GenericParameterAttributes.ReferenceTypeConstraint)).IsTrue();
    }
  }

  #endregion

  #region Query<T> Method Generic Constraint Tests

  [Test]
  public async Task ILensQuery_TwoGeneric_QueryMethod_HasClassConstraintAsync() {
    // Arrange
    var type = typeof(ILensQuery<Model1, Model2>);
    var queryMethod = type.GetMethod("Query")!;
    var genericArg = queryMethod.GetGenericArguments()[0];

    // Act
    var attributes = genericArg.GenericParameterAttributes;

    // Assert
    await Assert.That(attributes.HasFlag(
        System.Reflection.GenericParameterAttributes.ReferenceTypeConstraint)).IsTrue();
  }

  [Test]
  public async Task ILensQuery_TwoGeneric_GetByIdAsyncMethod_HasClassConstraintAsync() {
    // Arrange
    var type = typeof(ILensQuery<Model1, Model2>);
    var getByIdMethod = type.GetMethod("GetByIdAsync")!;
    var genericArg = getByIdMethod.GetGenericArguments()[0];

    // Act
    var attributes = genericArg.GenericParameterAttributes;

    // Assert
    await Assert.That(attributes.HasFlag(
        System.Reflection.GenericParameterAttributes.ReferenceTypeConstraint)).IsTrue();
  }

  #endregion

  #region Higher Arity Interfaces (6-10 type parameters)

  [Test]
  public async Task ILensQuery_SixGeneric_ExistsAsync() {
    // This test verifies the 6-parameter variant exists
    // The actual type will be created in implementation
    var sixGenericType = Type.GetType(
        "Whizbang.Core.Lenses.ILensQuery`6, Whizbang.Core");

    await Assert.That(sixGenericType).IsNotNull();
  }

  [Test]
  public async Task ILensQuery_SevenGeneric_ExistsAsync() {
    var sevenGenericType = Type.GetType(
        "Whizbang.Core.Lenses.ILensQuery`7, Whizbang.Core");

    await Assert.That(sevenGenericType).IsNotNull();
  }

  [Test]
  public async Task ILensQuery_EightGeneric_ExistsAsync() {
    var eightGenericType = Type.GetType(
        "Whizbang.Core.Lenses.ILensQuery`8, Whizbang.Core");

    await Assert.That(eightGenericType).IsNotNull();
  }

  [Test]
  public async Task ILensQuery_NineGeneric_ExistsAsync() {
    var nineGenericType = Type.GetType(
        "Whizbang.Core.Lenses.ILensQuery`9, Whizbang.Core");

    await Assert.That(nineGenericType).IsNotNull();
  }

  [Test]
  public async Task ILensQuery_TenGeneric_ExistsAsync() {
    var tenGenericType = Type.GetType(
        "Whizbang.Core.Lenses.ILensQuery`10, Whizbang.Core");

    await Assert.That(tenGenericType).IsNotNull();
  }

  #endregion
}
