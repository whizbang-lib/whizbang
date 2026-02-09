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
    await Assert.That(options.DefaultScope).IsEqualTo(GraphQLLensScope.DataOnly);
    await Assert.That(options.DefaultPageSize).IsEqualTo(10);
    await Assert.That(options.MaxPageSize).IsEqualTo(100);
    await Assert.That(options.IncludeMetadataInFilters).IsTrue();
    await Assert.That(options.IncludeScopeInFilters).IsTrue();
  }

  [Test]
  public async Task DefaultScope_ShouldBeSettableAsync() {
    // Arrange
    var options = new WhizbangGraphQLOptions();

    // Act
    options.DefaultScope = GraphQLLensScope.All;

    // Assert
    await Assert.That(options.DefaultScope).IsEqualTo(GraphQLLensScope.All);
  }

  [Test]
  public async Task DefaultScope_ShouldAcceptComposedFlagsAsync() {
    // Arrange
    var options = new WhizbangGraphQLOptions();

    // Act
    options.DefaultScope = GraphQLLensScope.Data | GraphQLLensScope.SystemFields;
    var hasData = options.DefaultScope.HasFlag(GraphQLLensScope.Data);
    var hasSystemFields = options.DefaultScope.HasFlag(GraphQLLensScope.SystemFields);
    var hasMetadata = options.DefaultScope.HasFlag(GraphQLLensScope.Metadata);
    var hasScope = options.DefaultScope.HasFlag(GraphQLLensScope.Scope);

    // Assert
    await Assert.That(hasData).IsTrue();
    await Assert.That(hasSystemFields).IsTrue();
    await Assert.That(hasMetadata).IsFalse();
    await Assert.That(hasScope).IsFalse();
  }

  [Test]
  public async Task DefaultPageSize_ShouldBeSettableAsync() {
    // Arrange
    var options = new WhizbangGraphQLOptions();

    // Act
    options.DefaultPageSize = 25;

    // Assert
    await Assert.That(options.DefaultPageSize).IsEqualTo(25);
  }

  [Test]
  public async Task MaxPageSize_ShouldBeSettableAsync() {
    // Arrange
    var options = new WhizbangGraphQLOptions();

    // Act
    options.MaxPageSize = 500;

    // Assert
    await Assert.That(options.MaxPageSize).IsEqualTo(500);
  }

  [Test]
  public async Task IncludeMetadataInFilters_ShouldBeSettableAsync() {
    // Arrange
    var options = new WhizbangGraphQLOptions();

    // Act
    options.IncludeMetadataInFilters = false;

    // Assert
    await Assert.That(options.IncludeMetadataInFilters).IsFalse();
  }

  [Test]
  public async Task IncludeScopeInFilters_ShouldBeSettableAsync() {
    // Arrange
    var options = new WhizbangGraphQLOptions();

    // Act
    options.IncludeScopeInFilters = false;

    // Assert
    await Assert.That(options.IncludeScopeInFilters).IsFalse();
  }

  [Test]
  public async Task AllPropertiesSet_ShouldRetainValuesAsync() {
    // Arrange & Act
    var options = new WhizbangGraphQLOptions {
      DefaultScope = GraphQLLensScope.Data | GraphQLLensScope.Metadata,
      DefaultPageSize = 50,
      MaxPageSize = 200,
      IncludeMetadataInFilters = false,
      IncludeScopeInFilters = false
    };

    // Assert
    await Assert.That(options.DefaultScope).IsEqualTo(GraphQLLensScope.Data | GraphQLLensScope.Metadata);
    await Assert.That(options.DefaultPageSize).IsEqualTo(50);
    await Assert.That(options.MaxPageSize).IsEqualTo(200);
    await Assert.That(options.IncludeMetadataInFilters).IsFalse();
    await Assert.That(options.IncludeScopeInFilters).IsFalse();
  }

  [Test]
  public async Task DefaultPageSize_Zero_ShouldBeAllowedAsync() {
    // Arrange
    var options = new WhizbangGraphQLOptions();

    // Act
    options.DefaultPageSize = 0;

    // Assert
    await Assert.That(options.DefaultPageSize).IsEqualTo(0);
  }

  [Test]
  public async Task MaxPageSize_LessThanDefaultPageSize_ShouldBeAllowedAsync() {
    // Arrange - options class doesn't enforce validation, that's done at runtime
    var options = new WhizbangGraphQLOptions();

    // Act
    options.DefaultPageSize = 100;
    options.MaxPageSize = 50;

    // Assert - values are stored as-is (validation at runtime)
    await Assert.That(options.DefaultPageSize).IsEqualTo(100);
    await Assert.That(options.MaxPageSize).IsEqualTo(50);
  }
}
