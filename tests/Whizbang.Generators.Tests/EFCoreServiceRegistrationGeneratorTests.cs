using TUnit.Assertions.Extensions;
using Whizbang.Generators.Tests;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for EFCoreServiceRegistrationGenerator attribute-based DbContext discovery.
/// Validates attribute discovery, key matching, and generated code structure.
/// </summary>
public class EFCoreServiceRegistrationGeneratorTests {

  #region Attribute Discovery Tests

  /// <summary>
  /// Test that a DbContext with [WhizbangDbContext] attribute is discovered.
  /// Explicit opt-in is required for DbContext participation.
  /// </summary>
  [Test]
  public async Task Generator_WithWhizbangDbContextAttribute_DiscoversDbContextAsync() {
    // Arrange
    var source = """
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      // Test event
      public record TestEvent : IEvent;

      // Test model
      public record TestModel {
        public string Id { get; init; } = "";
      }

      // Test perspective (requires IPerspectiveStore<TModel> in constructor)
      public class TestPerspective : IPerspectiveOf<TestEvent> {
        private readonly IPerspectiveStore<TestModel> _store;

        public TestPerspective(IPerspectiveStore<TestModel> store) {
          _store = store;
        }

        public Task Update(TestEvent @event, CancellationToken cancellationToken = default) {
          return Task.CompletedTask;
        }
      }

      [WhizbangDbContext]
      public class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert - should generate partial class for TestDbContext
    var partialClass = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("TestDbContext.Generated"));
    await Assert.That(partialClass).IsNotNull();

    // Should generate registration metadata
    var registration = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("EFCoreModelRegistration"));
    await Assert.That(registration).IsNotNull();
  }

  /// <summary>
  /// Test that a DbContext WITHOUT [WhizbangDbContext] attribute is NOT discovered.
  /// Explicit opt-in is required - no attribute = no participation.
  /// </summary>
  [Test]
  public async Task Generator_WithoutWhizbangDbContextAttribute_DoesNotDiscoverDbContextAsync() {
    // Arrange
    var source = """
      using Microsoft.EntityFrameworkCore;

      namespace TestApp;

      public class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert - should NOT generate any code (no DbContext discovered)
    var partialClass = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("TestDbContext.Generated"));
    await Assert.That(partialClass).IsNull();
  }

  /// <summary>
  /// Test that a DbContext with default key (no args) uses empty string as key.
  /// Default key matches perspectives with no [WhizbangPerspective] attribute.
  /// </summary>
  [Test]
  public async Task Generator_WithDefaultKey_UsesEmptyStringKeyAsync() {
    // Arrange
    var source = """
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      [WhizbangDbContext]  // No args = default key ""
      public class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert - should generate code with keys: [""]
    var partialClass = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("TestDbContext.Generated"));
    await Assert.That(partialClass).IsNotNull();

    var sourceText = partialClass!.SourceText.ToString();
    await Assert.That(sourceText).Contains("DbContext keys: [\"\"]");
  }

  /// <summary>
  /// Test that a DbContext with single key is correctly discovered.
  /// Single-key DbContext matches perspectives with that key.
  /// </summary>
  [Test]
  public async Task Generator_WithSingleKey_DiscoversDbContextWithKeyAsync() {
    // Arrange
    var source = """
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      [WhizbangDbContext("catalog")]
      public class CatalogDbContext : DbContext {
        public CatalogDbContext(DbContextOptions<CatalogDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert - should generate code with keys: ["catalog"]
    var partialClass = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("CatalogDbContext.Generated"));
    await Assert.That(partialClass).IsNotNull();

    var sourceText = partialClass!.SourceText.ToString();
    await Assert.That(sourceText).Contains("DbContext keys: [\"catalog\"]");
  }

  /// <summary>
  /// Test that a DbContext with multiple keys is correctly discovered.
  /// Multi-key DbContext matches perspectives with ANY matching key.
  /// </summary>
  [Test]
  public async Task Generator_WithMultipleKeys_DiscoversDbContextWithAllKeysAsync() {
    // Arrange
    var source = """
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      [WhizbangDbContext("catalog", "products")]
      public class CatalogDbContext : DbContext {
        public CatalogDbContext(DbContextOptions<CatalogDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert - should generate code with keys: ["catalog", "products"]
    var partialClass = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("CatalogDbContext.Generated"));
    await Assert.That(partialClass).IsNotNull();

    var sourceText = partialClass!.SourceText.ToString();
    await Assert.That(sourceText).Contains("DbContext keys: [\"catalog\", \"products\"]");
  }

  #endregion

  #region Generated Code Structure Tests

  /// <summary>
  /// Test that generator produces DbContext partial class with correct structure.
  /// Partial class should contain DbSet properties for discovered perspectives.
  /// </summary>
  [Test]
  public async Task Generator_WithDiscoveredDbContext_GeneratesPartialClassAsync() {
    // Arrange
    var source = """
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      [WhizbangDbContext]
      public partial class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert
    var partialClass = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("TestDbContext.Generated"));
    await Assert.That(partialClass).IsNotNull();

    var sourceText = partialClass!.SourceText.ToString();

    // Should have correct namespace
    await Assert.That(sourceText).Contains("namespace TestApp");

    // Should be partial class
    await Assert.That(sourceText).Contains("public partial class TestDbContext");

    // Should have auto-generated header
    await Assert.That(sourceText).Contains("// <auto-generated/>");
  }

  /// <summary>
  /// Test that generator produces registration metadata file.
  /// Registration metadata enables runtime model registration.
  /// </summary>
  [Test]
  public async Task Generator_WithDiscoveredDbContext_GeneratesRegistrationMetadataAsync() {
    // Arrange
    var source = """
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      [WhizbangDbContext]
      public class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert
    var registration = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("EFCoreModelRegistration"));
    await Assert.That(registration).IsNotNull();

    var sourceText = registration!.SourceText.ToString();

    // Should have GeneratedModelRegistration class
    await Assert.That(sourceText).Contains("public static class GeneratedModelRegistration");

    // Should have Initialize method
    await Assert.That(sourceText).Contains("public static void Initialize()");

    // Should have ModuleInitializer attribute
    await Assert.That(sourceText).Contains("[ModuleInitializer]");
  }

  /// <summary>
  /// Test that generator produces schema extensions file.
  /// Schema extensions provide EnsureWhizbangTablesCreatedAsync method.
  /// </summary>
  [Test]
  public async Task Generator_WithDiscoveredDbContext_GeneratesSchemaExtensionsAsync() {
    // Arrange
    var source = """
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      [WhizbangDbContext]
      public class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert
    var schemaExtensions = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("SchemaExtensions"));
    await Assert.That(schemaExtensions).IsNotNull();

    var sourceText = schemaExtensions!.SourceText.ToString();

    // Should have extension class
    await Assert.That(sourceText).Contains("public static class");
    await Assert.That(sourceText).Contains("SchemaExtensions");

    // Should have EnsureWhizbangTablesCreatedAsync method
    await Assert.That(sourceText).Contains("EnsureWhizbangTablesCreatedAsync");
  }

  #endregion

  #region No Generators Diagnostic Tests

  /// <summary>
  /// Test that generator reports no errors when DbContext is discovered.
  /// Successful discovery should not produce any diagnostics.
  /// </summary>
  [Test]
  public async Task Generator_WithValidDbContext_ProducesNoDiagnosticsAsync() {
    // Arrange
    var source = """
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      [WhizbangDbContext]
      public class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert - should have no errors
    var errors = result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();
  }

  #endregion
}
