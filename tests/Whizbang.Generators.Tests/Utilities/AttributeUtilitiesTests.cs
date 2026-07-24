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
    const string source = """

using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestAttribute : Attribute {
    public string? Route { get; set; }
}

[Test(Route = "/api/orders")]
public class TestClass { }
""";

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
    const string source = @"
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
    const string source = """

using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestAttribute : Attribute {
    public string? Route { get; set; }
}

[Test(Route = "/api/orders")]
public class TestClass { }
""";

    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestClass")!;
    var attribute = typeSymbol.GetAttributes()[0];

    // Act
    var result = AttributeUtilities.GetStringValue(attribute, "NonExistent");

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetStringValue_ConstructorArgument_ReturnsValueAsync() {
    // Arrange - Attribute with constructor parameter
    const string source = """

using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestAttribute : Attribute {
    public string Tag { get; }
    public TestAttribute(string tag) { Tag = tag; }
}

[Test("tenants")]
public class TestClass { }
""";

    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestClass")!;
    var attribute = typeSymbol.GetAttributes()[0];

    // Act
    var result = AttributeUtilities.GetStringValue(attribute, "Tag");

    // Assert
    await Assert.That(result).IsEqualTo("tenants");
  }

  [Test]
  public async Task GetStringValue_BothPresent_NamedTakesPrecedenceAsync() {
    // Arrange - Attribute with both constructor and named argument
    const string source = """

using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestAttribute : Attribute {
    public string Tag { get; set; }
    public TestAttribute(string tag) { Tag = tag; }
}

[Test("constructor-value", Tag = "named-value")]
public class TestClass { }
""";

    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestClass")!;
    var attribute = typeSymbol.GetAttributes()[0];

    // Act
    var result = AttributeUtilities.GetStringValue(attribute, "Tag");

    // Assert - Named argument should take precedence
    await Assert.That(result).IsEqualTo("named-value");
  }

  [Test]
  public async Task GetStringValue_CaseInsensitiveMatch_ReturnsValueAsync() {
    // Arrange - Constructor param "tag" should match property "Tag"
    const string source = """

using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestAttribute : Attribute {
    public string Tag { get; }
    public TestAttribute(string tag) { Tag = tag; }
}

[Test("my-tag")]
public class TestClass { }
""";

    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestClass")!;
    var attribute = typeSymbol.GetAttributes()[0];

    // Act - Property name is "Tag" but constructor param is "tag"
    var result = AttributeUtilities.GetStringValue(attribute, "Tag");

    // Assert
    await Assert.That(result).IsEqualTo("my-tag");
  }

  #endregion

  #region GetBoolValue Tests

  [Test]
  public async Task GetBoolValue_ExistingProperty_ReturnsValueAsync() {
    // Arrange
    const string source = @"
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
    const string source = @"
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
    const string source = @"
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

  [Test]
  public async Task GetBoolValue_ConstructorArgument_ReturnsValueAsync() {
    // Arrange - Attribute with constructor parameter
    const string source = @"
using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestAttribute : Attribute {
    public bool IncludeEvent { get; }
    public TestAttribute(bool includeEvent) { IncludeEvent = includeEvent; }
}

[Test(true)]
public class TestClass { }";

    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestClass")!;
    var attribute = typeSymbol.GetAttributes()[0];

    // Act
    var result = AttributeUtilities.GetBoolValue(attribute, "IncludeEvent", defaultValue: false);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task GetBoolValue_BothPresent_NamedTakesPrecedenceAsync() {
    // Arrange
    const string source = @"
using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestAttribute : Attribute {
    public bool IncludeEvent { get; set; }
    public TestAttribute(bool includeEvent) { IncludeEvent = includeEvent; }
}

[Test(true, IncludeEvent = false)]
public class TestClass { }";

    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestClass")!;
    var attribute = typeSymbol.GetAttributes()[0];

    // Act
    var result = AttributeUtilities.GetBoolValue(attribute, "IncludeEvent", defaultValue: true);

    // Assert - Named argument should take precedence
    await Assert.That(result).IsFalse();
  }

  #endregion

  #region GetIntValue Tests

  [Test]
  public async Task GetIntValue_ExistingProperty_ReturnsValueAsync() {
    // Arrange
    const string source = @"
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
    const string source = @"
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
    const string source = @"
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

  [Test]
  public async Task GetIntValue_ConstructorArgument_ReturnsValueAsync() {
    // Arrange - Attribute with constructor parameter
    const string source = @"
using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestAttribute : Attribute {
    public int Priority { get; }
    public TestAttribute(int priority) { Priority = priority; }
}

[Test(42)]
public class TestClass { }";

    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestClass")!;
    var attribute = typeSymbol.GetAttributes()[0];

    // Act
    var result = AttributeUtilities.GetIntValue(attribute, "Priority", defaultValue: 0);

    // Assert
    await Assert.That(result).IsEqualTo(42);
  }

  [Test]
  public async Task GetIntValue_BothPresent_NamedTakesPrecedenceAsync() {
    // Arrange
    const string source = @"
using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestAttribute : Attribute {
    public int Priority { get; set; }
    public TestAttribute(int priority) { Priority = priority; }
}

[Test(10, Priority = 99)]
public class TestClass { }";

    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestClass")!;
    var attribute = typeSymbol.GetAttributes()[0];

    // Act
    var result = AttributeUtilities.GetIntValue(attribute, "Priority", defaultValue: 0);

    // Assert - Named argument should take precedence
    await Assert.That(result).IsEqualTo(99);
  }

  #endregion

  #region GetStringArrayValue Tests

  [Test]
  public async Task GetStringArrayValue_NamedArgument_ReturnsValuesAsync() {
    // Arrange
    const string source = """

using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestAttribute : Attribute {
    public string[]? Properties { get; set; }
}

[Test(Properties = new[] { "Id", "Name", "Email" })]
public class TestClass { }
""";

    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestClass")!;
    var attribute = typeSymbol.GetAttributes()[0];

    // Act
    var result = AttributeUtilities.GetStringArrayValue(attribute, "Properties");

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).Count().IsEqualTo(3);
    await Assert.That(result[0]).IsEqualTo("Id");
    await Assert.That(result[1]).IsEqualTo("Name");
    await Assert.That(result[2]).IsEqualTo("Email");
  }

  [Test]
  public async Task GetStringArrayValue_ConstructorArgument_ReturnsValuesAsync() {
    // Arrange - Attribute with constructor parameter
    const string source = """

using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestAttribute : Attribute {
    public string[] Properties { get; }
    public TestAttribute(string[] properties) { Properties = properties; }
}

[Test(new[] { "TenantId", "UserId" })]
public class TestClass { }
""";

    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestClass")!;
    var attribute = typeSymbol.GetAttributes()[0];

    // Act
    var result = AttributeUtilities.GetStringArrayValue(attribute, "Properties");

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).Count().IsEqualTo(2);
    await Assert.That(result[0]).IsEqualTo("TenantId");
    await Assert.That(result[1]).IsEqualTo("UserId");
  }

  [Test]
  public async Task GetStringArrayValue_MissingProperty_ReturnsNullAsync() {
    // Arrange
    const string source = @"
using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestAttribute : Attribute {
    public string[]? Properties { get; set; }
}

[Test]
public class TestClass { }";

    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestClass")!;
    var attribute = typeSymbol.GetAttributes()[0];

    // Act
    var result = AttributeUtilities.GetStringArrayValue(attribute, "Properties");

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetStringArrayValue_EmptyArray_ReturnsEmptyArrayAsync() {
    // Arrange
    const string source = @"
using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestAttribute : Attribute {
    public string[]? Properties { get; set; }
}

[Test(Properties = new string[] { })]
public class TestClass { }";

    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestClass")!;
    var attribute = typeSymbol.GetAttributes()[0];

    // Act
    var result = AttributeUtilities.GetStringArrayValue(attribute, "Properties");

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task GetStringArrayValue_BothPresent_NamedTakesPrecedenceAsync() {
    // Arrange
    const string source = """

using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestAttribute : Attribute {
    public string[] Properties { get; set; }
    public TestAttribute(string[] properties) { Properties = properties; }
}

[Test(new[] { "FromCtor" }, Properties = new[] { "FromNamed" })]
public class TestClass { }
""";

    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestClass")!;
    var attribute = typeSymbol.GetAttributes()[0];

    // Act
    var result = AttributeUtilities.GetStringArrayValue(attribute, "Properties");

    // Assert - Named argument should take precedence
    await Assert.That(result).IsNotNull();
    await Assert.That(result).Count().IsEqualTo(1);
    await Assert.That(result[0]).IsEqualTo("FromNamed");
  }

  #endregion
}
