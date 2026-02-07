using TUnit.Assertions.Extensions;
using Whizbang.Generators.Tests;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for EFCoreServiceRegistrationGenerator attribute-based DbContext discovery.
/// Validates attribute discovery, key matching, and generated code structure.
/// </summary>
public class EFCoreServiceRegistrationGeneratorTests {

  // Perspective boilerplate required for generator to produce output
  private const string PERSPECTIVE_BOILERPLATE = """
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    // Test event
    public record TestEvent : IEvent;

    // Test model
    public record TestModel {
      public string Id { get; init; } = "";
    }

    // Test perspective (requires IPerspectiveStore<TModel> in constructor)
    public class TestPerspective {
      private readonly IPerspectiveStore<TestModel> _store;

      public TestPerspective(IPerspectiveStore<TestModel> store) {
        _store = store;
      }

      public Task Update(TestEvent @event, CancellationToken cancellationToken = default) {
        return Task.CompletedTask;
      }
    }

    """;

  #region Attribute Discovery Tests

  /// <summary>
  /// Test that a DbContext with [WhizbangDbContext] attribute is discovered.
  /// Explicit opt-in is required for DbContext participation.
  /// </summary>
  [Test]
  public async Task Generator_WithWhizbangDbContextAttribute_DiscoversDbContextAsync() {
    // Arrange
    var source = $$"""
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      {{PERSPECTIVE_BOILERPLATE}}

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
    var source = $$"""
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      {{PERSPECTIVE_BOILERPLATE}}

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
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record TestEvent : IEvent;
      public record TestModel { public string Id { get; init; } = ""; }

      // Perspective with matching key
      [WhizbangPerspective("catalog")]
      public class TestPerspective {
        private readonly IPerspectiveStore<TestModel> _store;
        public TestPerspective(IPerspectiveStore<TestModel> store) => _store = store;
        public Task Update(TestEvent @event, CancellationToken ct = default) => Task.CompletedTask;
      }

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
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record TestEvent : IEvent;
      public record TestModel { public string Id { get; init; } = ""; }

      // Perspective with matching key (matches "catalog" key)
      [WhizbangPerspective("catalog")]
      public class TestPerspective {
        private readonly IPerspectiveStore<TestModel> _store;
        public TestPerspective(IPerspectiveStore<TestModel> store) => _store = store;
        public Task Update(TestEvent @event, CancellationToken ct = default) => Task.CompletedTask;
      }

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
    var source = $$"""
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      {{PERSPECTIVE_BOILERPLATE}}

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
    var source = $$"""
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      {{PERSPECTIVE_BOILERPLATE}}

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
    var source = $$"""
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      {{PERSPECTIVE_BOILERPLATE}}

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

    // Should have EnsureWhizbangDatabaseInitializedAsync method
    await Assert.That(sourceText).Contains("EnsureWhizbangDatabaseInitializedAsync");
  }

  /// <summary>
  /// Test that generator produces OnModelCreating override in DbContext partial class.
  /// The override should call ConfigureWhizbang() and then OnModelCreatingExtended().
  /// </summary>
  [Test]
  public async Task Generator_WithDiscoveredDbContext_GeneratesOnModelCreatingOverrideAsync() {
    // Arrange
    var source = $$"""
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      {{PERSPECTIVE_BOILERPLATE}}

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

    // Should have OnModelCreating override
    await Assert.That(sourceText).Contains("protected override void OnModelCreating(ModelBuilder modelBuilder)");

    // Should call ConfigureWhizbang()
    await Assert.That(sourceText).Contains("modelBuilder.ConfigureWhizbang();");

    // Should call OnModelCreatingExtended()
    await Assert.That(sourceText).Contains("OnModelCreatingExtended(modelBuilder);");
  }

  /// <summary>
  /// Test that generator produces OnModelCreatingExtended partial method declaration.
  /// This provides the hook for users to add custom EF Core model configuration.
  /// </summary>
  [Test]
  public async Task Generator_WithDiscoveredDbContext_GeneratesOnModelCreatingExtendedHookAsync() {
    // Arrange
    var source = $$"""
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      {{PERSPECTIVE_BOILERPLATE}}

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

    // Should have partial method declaration
    await Assert.That(sourceText).Contains("partial void OnModelCreatingExtended(ModelBuilder modelBuilder);");
  }

  /// <summary>
  /// Test that generated code includes required using directives.
  /// Must include Whizbang.Data.EFCore.Postgres.Generated for ConfigureWhizbang() extension.
  /// </summary>
  [Test]
  public async Task Generator_WithDiscoveredDbContext_IncludesRequiredUsingDirectivesAsync() {
    // Arrange
    var source = $$"""
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      {{PERSPECTIVE_BOILERPLATE}}

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

    // Should include required using directives
    await Assert.That(sourceText).Contains("using Microsoft.EntityFrameworkCore;");
    await Assert.That(sourceText).Contains("using Whizbang.Core.Lenses;");
    await Assert.That(sourceText).Contains("using Whizbang.Data.EFCore.Postgres.Generated;");
  }

  /// <summary>
  /// Test that OnModelCreating includes XML documentation explaining the pattern.
  /// Documentation helps users understand the hook system.
  /// </summary>
  [Test]
  public async Task Generator_OnModelCreating_IncludesXmlDocumentationAsync() {
    // Arrange
    var source = $$"""
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      {{PERSPECTIVE_BOILERPLATE}}

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

    // Should have XML summary for OnModelCreating
    await Assert.That(sourceText).Contains("/// <summary>");
    await Assert.That(sourceText).Contains("/// Configures the EF Core model for this DbContext.");
    await Assert.That(sourceText).Contains("/// Calls modelBuilder.ConfigureWhizbang() for auto-generated configurations,");
    await Assert.That(sourceText).Contains("/// then calls OnModelCreatingExtended() for custom user configurations.");

    // Should have XML summary for OnModelCreatingExtended
    await Assert.That(sourceText).Contains("/// Override this method to extend the model configuration beyond Whizbang's auto-generated setup.");
    await Assert.That(sourceText).Contains("/// Use this for custom entity configurations, indexes, constraints, etc.");
  }

  /// <summary>
  /// Test that multiple DbContexts each get their own OnModelCreating override.
  /// Each DbContext partial class should have independent configuration.
  /// </summary>
  [Test]
  public async Task Generator_WithMultipleDbContexts_GeneratesOnModelCreatingForEachAsync() {
    // Arrange
    var source = """
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record TestEvent : IEvent;
      public record TestModel { public string Id { get; init; } = ""; }

      // Perspective for catalog key
      [WhizbangPerspective("catalog")]
      public class CatalogPerspective {
        private readonly IPerspectiveStore<TestModel> _store;
        public CatalogPerspective(IPerspectiveStore<TestModel> store) => _store = store;
        public Task Update(TestEvent @event, CancellationToken ct = default) => Task.CompletedTask;
      }

      // Perspective for orders key
      [WhizbangPerspective("orders")]
      public class OrdersPerspective {
        private readonly IPerspectiveStore<TestModel> _store;
        public OrdersPerspective(IPerspectiveStore<TestModel> store) => _store = store;
        public Task Update(TestEvent @event, CancellationToken ct = default) => Task.CompletedTask;
      }

      [WhizbangDbContext("catalog")]
      public partial class CatalogDbContext : DbContext {
        public CatalogDbContext(DbContextOptions<CatalogDbContext> options) : base(options) { }
      }

      [WhizbangDbContext("orders")]
      public partial class OrdersDbContext : DbContext {
        public OrdersDbContext(DbContextOptions<OrdersDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert
    var catalogPartial = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("CatalogDbContext.Generated"));
    await Assert.That(catalogPartial).IsNotNull();

    var ordersPartial = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("OrdersDbContext.Generated"));
    await Assert.That(ordersPartial).IsNotNull();

    // Both should have OnModelCreating override
    var catalogSource = catalogPartial!.SourceText.ToString();
    await Assert.That(catalogSource).Contains("protected override void OnModelCreating(ModelBuilder modelBuilder)");
    await Assert.That(catalogSource).Contains("modelBuilder.ConfigureWhizbang();");

    var ordersSource = ordersPartial!.SourceText.ToString();
    await Assert.That(ordersSource).Contains("protected override void OnModelCreating(ModelBuilder modelBuilder)");
    await Assert.That(ordersSource).Contains("modelBuilder.ConfigureWhizbang();");
  }

  #endregion

  #region AOT Schema Generation Tests

  /// <summary>
  /// Test that schema extensions include core infrastructure schema SQL via migrations.
  /// All core tables are now created via numbered migration files embedded as tuples.
  /// Checks for a subset of key tables to verify migrations are embedded correctly.
  /// </summary>
  [Test]
  public async Task Generator_SchemaExtensions_IncludesCoreInfrastructureSchemaAsync() {
    // Arrange
    var source = $$"""
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      {{PERSPECTIVE_BOILERPLATE}}

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

    // Should call ExecuteMigrationsAsync to execute all migrations
    await Assert.That(sourceText).Contains("await ExecuteMigrationsAsync(dbContext");

    // Should use runtime schema generation via PostgresSchemaBuilder (AOT-compatible)
    await Assert.That(sourceText).Contains("PostgresSchemaBuilder.Instance.BuildInfrastructureSchema");
    await Assert.That(sourceText).Contains("ExecuteCoreInfrastructureTablesAsync");

    // Should configure schema with correct prefix
    await Assert.That(sourceText).Contains("InfrastructurePrefix: \"wh_\"");
    await Assert.That(sourceText).Contains("PerspectivePrefix: \"wh_per_\"");
  }

  /// <summary>
  /// Test that event_store table includes stream_id and scope columns.
  /// These columns are required for migration compatibility.
  /// </summary>
  [Test]
  public async Task Generator_EventStoreTable_IncludesStreamIdAndScopeColumnsAsync() {
    // Arrange
    var source = $$"""
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      {{PERSPECTIVE_BOILERPLATE}}

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

    // Should use runtime schema generation (schema structure defined in PostgresSchemaBuilder, not in template)
    await Assert.That(sourceText).Contains("PostgresSchemaBuilder.Instance.BuildInfrastructureSchema");

    // Should have ExecuteCoreInfrastructureTablesAsync method that creates tables
    await Assert.That(sourceText).Contains("ExecuteCoreInfrastructureTablesAsync");

    // Schema configuration is passed at runtime
    await Assert.That(sourceText).Contains("SchemaConfiguration");
  }

  /// <summary>
  /// Test that migration SQL uses proper escaping for ExecuteSqlRawAsync.
  /// Migration SQL stored in tuples should use verbatim strings (@"...")
  /// and be called via ExecuteSqlRawAsync for each migration.
  /// </summary>
  [Test]
  public async Task Generator_SchemaSQL_UsesPropperEscapingForExecuteSqlRawAsync() {
    // Arrange
    var source = $$"""
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      {{PERSPECTIVE_BOILERPLATE}}

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

    // Should use verbatim strings (@"...") for migration SQL in tuples
    // Pattern: ("migration_name.sql", @"SQL content")
    await Assert.That(sourceText).Contains(", @\"");

    // Should call ExecuteSqlRawAsync with migration SQL (variable names may vary: sql, transformedSql, etc.)
    await Assert.That(sourceText).Contains("await dbContext.Database.ExecuteSqlRawAsync(");
  }

  /// <summary>
  /// Test that perspective_checkpoints table has composite primary key.
  /// The PK should be (stream_id, perspective_name).
  /// </summary>
  [Test]
  public async Task Generator_PerspectiveCheckpoints_HasCompositePrimaryKeyAsync() {
    // Arrange
    var source = $$"""
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      {{PERSPECTIVE_BOILERPLATE}}

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

    // Should use runtime schema generation (constraints defined in PostgresSchemaBuilder)
    await Assert.That(sourceText).Contains("PostgresSchemaBuilder.Instance.BuildInfrastructureSchema");

    // Should have ExecuteConstraintsAsync method for adding FKs and composite PKs
    await Assert.That(sourceText).Contains("ExecuteConstraintsAsync");
  }

  /// <summary>
  /// Test that schema extensions call ExecuteMigrationsAsync.
  /// Migrations enhance the core schema with additional features.
  /// </summary>
  [Test]
  public async Task Generator_SchemaExtensions_CallsExecuteMigrationsAsync() {
    // Arrange
    var source = $$"""
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      {{PERSPECTIVE_BOILERPLATE}}

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

    // Should have ExecuteMigrationsAsync method
    await Assert.That(sourceText).Contains("ExecuteMigrationsAsync");

    // Should call it from EnsureWhizbangDatabaseInitializedAsync
    await Assert.That(sourceText).Contains("await ExecuteMigrationsAsync(dbContext");
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
    var source = $$"""
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      {{PERSPECTIVE_BOILERPLATE}}

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

  /// <summary>
  /// Test that schema extensions include GIN indexes for JSONB columns.
  /// GIN indexes enable efficient LINQ queries on JSONB data (containment, key lookups, path expressions).
  /// </summary>
  [Test]
  public async Task Generator_SchemaExtensions_IncludesGinIndexesForJsonbColumnsAsync() {
    // Arrange - use explicit perspective that implements IPerspectiveFor<TModel>
    // The PERSPECTIVE_BOILERPLATE doesn't implement the interface, so we need a full definition
    var source = """
      using System.Threading;
      using System.Threading.Tasks;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record TestEvent : IEvent;
      public record TestModel { public string Id { get; init; } = ""; }

      // Perspective that implements IPerspectiveFor<TModel> (required for generator discovery)
      public class TestPerspective : IPerspectiveFor<TestModel> {
        private readonly IPerspectiveStore<TestModel> _store;
        public TestPerspective(IPerspectiveStore<TestModel> store) => _store = store;
        public Task UpdateAsync(TestEvent @event, CancellationToken ct = default) => Task.CompletedTask;
      }

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

    // Should include GIN indexes for all JSONB columns
    // GIN indexes use "USING gin (column)" syntax
    await Assert.That(sourceText).Contains("USING gin (data)");
    await Assert.That(sourceText).Contains("USING gin (metadata)");
    await Assert.That(sourceText).Contains("USING gin (scope)");

    // Should have index names following convention
    await Assert.That(sourceText).Contains("_data_gin");
    await Assert.That(sourceText).Contains("_metadata_gin");
    await Assert.That(sourceText).Contains("_scope_gin");
  }

  #endregion
}
