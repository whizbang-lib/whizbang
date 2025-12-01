using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema;

namespace Whizbang.Data.Schema.Tests;

/// <summary>
/// Tests for DefaultValue type system - pure enum-based approach for type safety.
/// Tests verify factory methods, pattern matching, and enum values.
/// </summary>
public class DefaultValueTests {
  // ===== DefaultValueFunction Enum Tests =====

  [Test]
  public async Task DefaultValueFunction_HasDateTimeNowAsync() {
    // Arrange & Act
    var hasValue = Enum.IsDefined(typeof(DefaultValueFunction), "DateTime_Now");

    // Assert
    await Assert.That(hasValue).IsTrue();
  }

  [Test]
  public async Task DefaultValueFunction_HasDateTimeUtcNowAsync() {
    // Arrange & Act
    var hasValue = Enum.IsDefined(typeof(DefaultValueFunction), "DateTime_UtcNow");

    // Assert
    await Assert.That(hasValue).IsTrue();
  }

  [Test]
  public async Task DefaultValueFunction_HasUuidGenerateAsync() {
    // Arrange & Act
    var hasValue = Enum.IsDefined(typeof(DefaultValueFunction), "Uuid_Generate");

    // Assert
    await Assert.That(hasValue).IsTrue();
  }

  [Test]
  public async Task DefaultValueFunction_HasBooleanTrueAsync() {
    // Arrange & Act
    var hasValue = Enum.IsDefined(typeof(DefaultValueFunction), "Boolean_True");

    // Assert
    await Assert.That(hasValue).IsTrue();
  }

  [Test]
  public async Task DefaultValueFunction_HasBooleanFalseAsync() {
    // Arrange & Act
    var hasValue = Enum.IsDefined(typeof(DefaultValueFunction), "Boolean_False");

    // Assert
    await Assert.That(hasValue).IsTrue();
  }

  [Test]
  public async Task DefaultValueFunction_HasExactlyFiveValuesAsync() {
    // Arrange & Act
    var valueCount = Enum.GetValues<DefaultValueFunction>().Length;

    // Assert
    await Assert.That(valueCount).IsEqualTo(5);
  }

  // ===== DefaultValue Factory Method Tests =====

  [Test]
  public async Task DefaultValue_FunctionFactory_ReturnsFunctionDefaultAsync() {
    // Arrange & Act
    var defaultValue = DefaultValue.Function(DefaultValueFunction.DateTime_Now);

    // Assert
    await Assert.That(defaultValue).IsNotNull();
    await Assert.That(defaultValue).IsTypeOf<FunctionDefault>();
  }

  [Test]
  public async Task DefaultValue_IntegerFactory_ReturnsIntegerDefaultAsync() {
    // Arrange & Act
    var defaultValue = DefaultValue.Integer(42);

    // Assert
    await Assert.That(defaultValue).IsNotNull();
    await Assert.That(defaultValue).IsTypeOf<IntegerDefault>();
  }

  [Test]
  public async Task DefaultValue_StringFactory_ReturnsStringDefaultAsync() {
    // Arrange & Act
    var defaultValue = DefaultValue.String("test");

    // Assert
    await Assert.That(defaultValue).IsNotNull();
    await Assert.That(defaultValue).IsTypeOf<StringDefault>();
  }

  [Test]
  public async Task DefaultValue_BooleanFactory_ReturnsBooleanDefaultAsync() {
    // Arrange & Act
    var defaultValue = DefaultValue.Boolean(true);

    // Assert
    await Assert.That(defaultValue).IsNotNull();
    await Assert.That(defaultValue).IsTypeOf<BooleanDefault>();
  }

  [Test]
  public async Task DefaultValue_Null_ReturnsNullDefaultAsync() {
    // Arrange & Act
    var defaultValue = DefaultValue.Null;

    // Assert
    await Assert.That(defaultValue).IsNotNull();
    await Assert.That(defaultValue).IsTypeOf<NullDefault>();
  }

  // ===== DefaultValue Value Preservation Tests =====

  [Test]
  public async Task FunctionDefault_PreservesFunctionValueAsync() {
    // Arrange
    var function = DefaultValueFunction.Uuid_Generate;

    // Act
    var defaultValue = DefaultValue.Function(function);

    // Assert
    await Assert.That(defaultValue).IsTypeOf<FunctionDefault>();
    var functionDefault = (FunctionDefault)defaultValue;
    await Assert.That(functionDefault.FunctionType).IsEqualTo(function);
  }

  [Test]
  public async Task IntegerDefault_PreservesIntegerValueAsync() {
    // Arrange
    var value = 123;

    // Act
    var defaultValue = DefaultValue.Integer(value);

    // Assert
    await Assert.That(defaultValue).IsTypeOf<IntegerDefault>();
    var integerDefault = (IntegerDefault)defaultValue;
    await Assert.That(integerDefault.Value).IsEqualTo(value);
  }

  [Test]
  public async Task StringDefault_PreservesStringValueAsync() {
    // Arrange
    var value = "Pending";

    // Act
    var defaultValue = DefaultValue.String(value);

    // Assert
    await Assert.That(defaultValue).IsTypeOf<StringDefault>();
    var stringDefault = (StringDefault)defaultValue;
    await Assert.That(stringDefault.Value).IsEqualTo(value);
  }

  [Test]
  public async Task BooleanDefault_PreservesBooleanValueAsync() {
    // Arrange
    var value = false;

    // Act
    var defaultValue = DefaultValue.Boolean(value);

    // Assert
    await Assert.That(defaultValue).IsTypeOf<BooleanDefault>();
    var booleanDefault = (BooleanDefault)defaultValue;
    await Assert.That(booleanDefault.Value).IsEqualTo(value);
  }

  // ===== DefaultValue Null Singleton Tests =====

  [Test]
  public async Task NullDefault_ReturnsSameSingletonInstanceAsync() {
    // Arrange & Act
    var null1 = DefaultValue.Null;
    var null2 = DefaultValue.Null;

    // Assert
    await Assert.That(ReferenceEquals(null1, null2)).IsTrue();
  }

  // ===== DefaultValue Record Value Equality Tests =====

  [Test]
  public async Task FunctionDefault_SameFunctionValue_AreEqualAsync() {
    // Arrange
    var default1 = DefaultValue.Function(DefaultValueFunction.DateTime_Now);
    var default2 = DefaultValue.Function(DefaultValueFunction.DateTime_Now);

    // Act & Assert
    await Assert.That(default1).IsEqualTo(default2);
  }

  [Test]
  public async Task FunctionDefault_DifferentFunctionValue_AreNotEqualAsync() {
    // Arrange
    var default1 = DefaultValue.Function(DefaultValueFunction.DateTime_Now);
    var default2 = DefaultValue.Function(DefaultValueFunction.DateTime_UtcNow);

    // Act & Assert
    await Assert.That(default1).IsNotEqualTo(default2);
  }

  [Test]
  public async Task IntegerDefault_SameValue_AreEqualAsync() {
    // Arrange
    var default1 = DefaultValue.Integer(42);
    var default2 = DefaultValue.Integer(42);

    // Act & Assert
    await Assert.That(default1).IsEqualTo(default2);
  }

  [Test]
  public async Task IntegerDefault_DifferentValue_AreNotEqualAsync() {
    // Arrange
    var default1 = DefaultValue.Integer(42);
    var default2 = DefaultValue.Integer(43);

    // Act & Assert
    await Assert.That(default1).IsNotEqualTo(default2);
  }

  [Test]
  public async Task StringDefault_SameValue_AreEqualAsync() {
    // Arrange
    var default1 = DefaultValue.String("test");
    var default2 = DefaultValue.String("test");

    // Act & Assert
    await Assert.That(default1).IsEqualTo(default2);
  }

  [Test]
  public async Task StringDefault_DifferentValue_AreNotEqualAsync() {
    // Arrange
    var default1 = DefaultValue.String("test1");
    var default2 = DefaultValue.String("test2");

    // Act & Assert
    await Assert.That(default1).IsNotEqualTo(default2);
  }

  [Test]
  public async Task BooleanDefault_SameValue_AreEqualAsync() {
    // Arrange
    var default1 = DefaultValue.Boolean(true);
    var default2 = DefaultValue.Boolean(true);

    // Act & Assert
    await Assert.That(default1).IsEqualTo(default2);
  }

  [Test]
  public async Task BooleanDefault_DifferentValue_AreNotEqualAsync() {
    // Arrange
    var default1 = DefaultValue.Boolean(true);
    var default2 = DefaultValue.Boolean(false);

    // Act & Assert
    await Assert.That(default1).IsNotEqualTo(default2);
  }

  // ===== DefaultValue Abstract Type Tests =====

  [Test]
  public async Task DefaultValue_IsAbstractTypeAsync() {
    // Arrange & Act
    var isAbstract = typeof(DefaultValue).IsAbstract;

    // Assert
    await Assert.That(isAbstract).IsTrue();
  }

  [Test]
  public async Task DefaultValue_IsRecordAsync() {
    // Arrange & Act - Records have compiler-generated EqualityContract property
    var hasEqualityContract = typeof(DefaultValue).GetProperty("EqualityContract",
      System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) != null;

    // Assert
    await Assert.That(hasEqualityContract).IsTrue();
  }

  [Test]
  public async Task FunctionDefault_IsSealedAsync() {
    // Arrange & Act
    var isSealed = typeof(FunctionDefault).IsSealed;

    // Assert
    await Assert.That(isSealed).IsTrue();
  }

  [Test]
  public async Task IntegerDefault_IsSealedAsync() {
    // Arrange & Act
    var isSealed = typeof(IntegerDefault).IsSealed;

    // Assert
    await Assert.That(isSealed).IsTrue();
  }

  [Test]
  public async Task StringDefault_IsSealedAsync() {
    // Arrange & Act
    var isSealed = typeof(StringDefault).IsSealed;

    // Assert
    await Assert.That(isSealed).IsTrue();
  }

  [Test]
  public async Task BooleanDefault_IsSealedAsync() {
    // Arrange & Act
    var isSealed = typeof(BooleanDefault).IsSealed;

    // Assert
    await Assert.That(isSealed).IsTrue();
  }

  [Test]
  public async Task NullDefault_IsSealedAsync() {
    // Arrange & Act
    var isSealed = typeof(NullDefault).IsSealed;

    // Assert
    await Assert.That(isSealed).IsTrue();
  }
}
