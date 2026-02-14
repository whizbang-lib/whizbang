extern alias shared;

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using AttributeUtilities = shared::Whizbang.Generators.Shared.Utilities.AttributeUtilities;

namespace Whizbang.Generators.Tests.Utilities;

/// <summary>
/// Unit tests for AttributeUtilities.
/// Tests attribute value extraction utilities used by all generators.
/// </summary>
public class AttributeUtilitiesTests {
  #region GetStringValue Tests

  [Test]
  public async Task GetStringValue_ExistingProperty_ReturnsValueAsync() {
    // Arrange - Create a compilation with an attribute that has a string property
    var source = @"
using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestAttribute : Attribute {
    public string? Route { get; set; }
}

[Test(Route = ""/api/orders"")]
public class TestClass { }";

    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestClass")!;
    var attribute = typeSymbol.GetAttributes()[0];

    // Act
    var result = AttributeUtilities.GetStringValue(attribute, "Route");

    // Assert
    await Assert.That(result).IsEqualTo("/api/orders");
  }

  [Test]
  public async Task GetStringValue_MissingProperty_ReturnsNullAsync() {
    // Arrange
    var source = @"
using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestAttribute : Attribute {
    public string? Route { get; set; }
}

[Test]
public class TestClass { }";

    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestClass")!;
    var attribute = typeSymbol.GetAttributes()[0];

    // Act
    var result = AttributeUtilities.GetStringValue(attribute, "Route");

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetStringValue_NonExistentProperty_ReturnsNullAsync() {
    // Arrange
    var source = @"
using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestAttribute : Attribute {
    public string? Route { get; set; }
}

[Test(Route = ""/api/orders"")]
public class TestClass { }";

    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestClass")!;
    var attribute = typeSymbol.GetAttributes()[0];

    // Act
    var result = AttributeUtilities.GetStringValue(attribute, "NonExistent");

    // Assert
    await Assert.That(result).IsNull();
  }

  #endregion

  #region GetBoolValue Tests

  [Test]
  public async Task GetBoolValue_ExistingProperty_ReturnsValueAsync() {
    // Arrange
    var source = @"
using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestAttribute : Attribute {
    public bool IsEnabled { get; set; }
}

[Test(IsEnabled = true)]
public class TestClass { }";

    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestClass")!;
    var attribute = typeSymbol.GetAttributes()[0];

    // Act
    var result = AttributeUtilities.GetBoolValue(attribute, "IsEnabled", defaultValue: false);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task GetBoolValue_MissingProperty_ReturnsDefaultAsync() {
    // Arrange
    var source = @"
using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestAttribute : Attribute {
    public bool IsEnabled { get; set; }
}

[Test]
public class TestClass { }";

    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestClass")!;
    var attribute = typeSymbol.GetAttributes()[0];

    // Act
    var result = AttributeUtilities.GetBoolValue(attribute, "IsEnabled", defaultValue: true);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task GetBoolValue_FalseValue_ReturnsFalseAsync() {
    // Arrange
    var source = @"
using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestAttribute : Attribute {
    public bool IsEnabled { get; set; }
}

[Test(IsEnabled = false)]
public class TestClass { }";

    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestClass")!;
    var attribute = typeSymbol.GetAttributes()[0];

    // Act
    var result = AttributeUtilities.GetBoolValue(attribute, "IsEnabled", defaultValue: true);

    // Assert
    await Assert.That(result).IsFalse();
  }

  #endregion

  #region GetIntValue Tests

  [Test]
  public async Task GetIntValue_ExistingProperty_ReturnsValueAsync() {
    // Arrange
    var source = @"
using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestAttribute : Attribute {
    public int MaxResults { get; set; }
}

[Test(MaxResults = 100)]
public class TestClass { }";

    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestClass")!;
    var attribute = typeSymbol.GetAttributes()[0];

    // Act
    var result = AttributeUtilities.GetIntValue(attribute, "MaxResults", defaultValue: 50);

    // Assert
    await Assert.That(result).IsEqualTo(100);
  }

  [Test]
  public async Task GetIntValue_MissingProperty_ReturnsDefaultAsync() {
    // Arrange
    var source = @"
using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestAttribute : Attribute {
    public int MaxResults { get; set; }
}

[Test]
public class TestClass { }";

    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestClass")!;
    var attribute = typeSymbol.GetAttributes()[0];

    // Act
    var result = AttributeUtilities.GetIntValue(attribute, "MaxResults", defaultValue: 50);

    // Assert
    await Assert.That(result).IsEqualTo(50);
  }

  [Test]
  public async Task GetIntValue_ZeroValue_ReturnsZeroAsync() {
    // Arrange
    var source = @"
using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestAttribute : Attribute {
    public int MaxResults { get; set; }
}

[Test(MaxResults = 0)]
public class TestClass { }";

    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestClass")!;
    var attribute = typeSymbol.GetAttributes()[0];

    // Act
    var result = AttributeUtilities.GetIntValue(attribute, "MaxResults", defaultValue: 50);

    // Assert
    await Assert.That(result).IsEqualTo(0);
  }

  #endregion
}
