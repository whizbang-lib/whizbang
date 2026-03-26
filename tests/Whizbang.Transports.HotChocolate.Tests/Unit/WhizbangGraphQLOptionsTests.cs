using Whizbang.Transports.HotChocolate;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// Tests for <see cref="WhizbangGraphQLOptions"/>.
/// Verifies configuration defaults and property behavior.
/// </summary>
public class WhizbangGraphQLOptionsTests {
  [Test]
  public async Task Constructor_ShouldSetDefaultsAsync() {
    // Arrange & Act
    var options = new WhizbangGraphQLOptions();

    // Assert
    await Assert.That(options.DefaultScope).IsEqualTo(GraphQLLensScopes.DataOnly);
    await Assert.That(options.DefaultPageSize).IsEqualTo(10);
    await Assert.That(options.MaxPageSize).IsEqualTo(100);
    await Assert.That(options.IncludeMetadataInFilters).IsTrue();
    await Assert.That(options.IncludeScopeInFilters).IsTrue();
  }

  [Test]
  public async Task DefaultScope_ShouldBeSettableAsync() {
    // Arrange
    var options = new WhizbangGraphQLOptions {
      // Act
      DefaultScope = GraphQLLensScopes.All
    };

    // Assert
    await Assert.That(options.DefaultScope).IsEqualTo(GraphQLLensScopes.All);
  }

  [Test]
  public async Task DefaultScope_ShouldAcceptComposedFlagsAsync() {
    // Arrange
    var options = new WhizbangGraphQLOptions {
      // Act
      DefaultScope = GraphQLLensScopes.Data | GraphQLLensScopes.SystemFields
    };
    var hasData = options.DefaultScope.HasFlag(GraphQLLensScopes.Data);
    var hasSystemFields = options.DefaultScope.HasFlag(GraphQLLensScopes.SystemFields);
    var hasMetadata = options.DefaultScope.HasFlag(GraphQLLensScopes.Metadata);
    var hasScope = options.DefaultScope.HasFlag(GraphQLLensScopes.Scope);

    // Assert
    await Assert.That(hasData).IsTrue();
    await Assert.That(hasSystemFields).IsTrue();
    await Assert.That(hasMetadata).IsFalse();
    await Assert.That(hasScope).IsFalse();
  }

  [Test]
  public async Task DefaultPageSize_ShouldBeSettableAsync() {
    // Arrange
    var options = new WhizbangGraphQLOptions {
      // Act
      DefaultPageSize = 25
    };

    // Assert
    await Assert.That(options.DefaultPageSize).IsEqualTo(25);
  }

  [Test]
  public async Task MaxPageSize_ShouldBeSettableAsync() {
    // Arrange
    var options = new WhizbangGraphQLOptions {
      // Act
      MaxPageSize = 500
    };

    // Assert
    await Assert.That(options.MaxPageSize).IsEqualTo(500);
  }

  [Test]
  public async Task IncludeMetadataInFilters_ShouldBeSettableAsync() {
    // Arrange
    var options = new WhizbangGraphQLOptions {
      // Act
      IncludeMetadataInFilters = false
    };

    // Assert
    await Assert.That(options.IncludeMetadataInFilters).IsFalse();
  }

  [Test]
  public async Task IncludeScopeInFilters_ShouldBeSettableAsync() {
    // Arrange
    var options = new WhizbangGraphQLOptions {
      // Act
      IncludeScopeInFilters = false
    };

    // Assert
    await Assert.That(options.IncludeScopeInFilters).IsFalse();
  }

  [Test]
  public async Task AllPropertiesSet_ShouldRetainValuesAsync() {
    // Arrange & Act
    var options = new WhizbangGraphQLOptions {
      DefaultScope = GraphQLLensScopes.Data | GraphQLLensScopes.Metadata,
      DefaultPageSize = 50,
      MaxPageSize = 200,
      IncludeMetadataInFilters = false,
      IncludeScopeInFilters = false
    };

    // Assert
    await Assert.That(options.DefaultScope).IsEqualTo(GraphQLLensScopes.Data | GraphQLLensScopes.Metadata);
    await Assert.That(options.DefaultPageSize).IsEqualTo(50);
    await Assert.That(options.MaxPageSize).IsEqualTo(200);
    await Assert.That(options.IncludeMetadataInFilters).IsFalse();
    await Assert.That(options.IncludeScopeInFilters).IsFalse();
  }

  [Test]
  public async Task DefaultPageSize_Zero_ShouldBeAllowedAsync() {
    // Arrange
    var options = new WhizbangGraphQLOptions {
      // Act
      DefaultPageSize = 0
    };

    // Assert
    await Assert.That(options.DefaultPageSize).IsEqualTo(0);
  }

  [Test]
  public async Task MaxPageSize_LessThanDefaultPageSize_ShouldBeAllowedAsync() {
    // Arrange - options class doesn't enforce validation, that's done at runtime
    var options = new WhizbangGraphQLOptions {
      // Act
      DefaultPageSize = 100,
      MaxPageSize = 50
    };

    // Assert - values are stored as-is (validation at runtime)
    await Assert.That(options.DefaultPageSize).IsEqualTo(100);
    await Assert.That(options.MaxPageSize).IsEqualTo(50);
  }
}
