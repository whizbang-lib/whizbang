using TUnit.Assertions.Extensions;
using Whizbang.Generators.Tests;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for EFCoreServiceRegistrationGenerator attribute-based DbContext discovery.
/// Validates attribute discovery, key matching, and generated code structure.
/// </summary>
public class EFCoreServiceRegistrationGeneratorTests {

  // Perspective boilerplate required for generator to produce output
  // NOTE: The perspective MUST implement IPerspectiveFor<TModel, TEvent> interface(s)
  // for the EFCorePerspectiveAssociationGenerator to detect it
  private const string PERSPECTIVE_BOILERPLATE = """
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    // Test event
    public record TestEvent : IEvent;

    // Test model
    public record TestModel {
      public string Id { get; init; } = "";
    }

    // Test perspective implementing IPerspectiveFor interface for generator detection
    // The Apply method signature must match the interface: TModel Apply(TModel currentData, TEvent eventData)
    public class TestPerspective : IPerspectiveFor<TestModel, TestEvent> {
      private readonly IPerspectiveStore<TestModel> _store;

      public TestPerspective(IPerspectiveStore<TestModel> store) {
        _store = store;
      }

      // Interface requires non-nullable TModel first parameter
      public TestModel Apply(TestModel currentData, TestEvent eventData) {
        return currentData with { Id = "updated" };
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
    const string source = """
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
    const string source = """
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
    const string source = """
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

    // Should include required using directives (now with global:: prefix for consistency)
    await Assert.That(sourceText).Contains("using Microsoft.EntityFrameworkCore;");
    await Assert.That(sourceText).Contains("using global::Whizbang.Core.Lenses;");
    await Assert.That(sourceText).Contains("using global::Whizbang.Data.EFCore.Postgres.Generated;");
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
    const string source = """
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
  /// Test that perspective_cursors table has composite primary key.
  /// The PK should be (stream_id, perspective_name).
  /// </summary>
  [Test]
  public async Task Generator_PerspectiveCursors_HasCompositePrimaryKeyAsync() {
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

  [Test]
  public async Task Generator_SchemaExtensions_UsesXactLockWithTransactionAndRetryAsync() {
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

    // Should use transaction-level advisory lock (PgBouncer-safe), NOT session-level
    await Assert.That(sourceText).Contains("pg_try_advisory_xact_lock");
    // Should NOT use session-level lock or manual unlock
    await Assert.That(sourceText).DoesNotContain("SELECT pg_try_advisory_lock(");
    await Assert.That(sourceText).DoesNotContain("pg_advisory_unlock");

    // Should use BeginTransactionAsync for PgBouncer compatibility
    await Assert.That(sourceText).Contains("BeginTransactionAsync");

    // Should have fast path with bulk hash comparison
    await Assert.That(sourceText).Contains("_bulkGetHashesAsync");
    await Assert.That(sourceText).Contains("_compareHashes");

    // Should have outer retry loop with _isRetryableError
    await Assert.That(sourceText).Contains("_isRetryableError");

    // Should set 600s command timeout for DDL
    await Assert.That(sourceText).Contains("600");

    // Should have owner column for migration categorization
    await Assert.That(sourceText).Contains("owner");

    // Should reference SchemaInitializationLog for structured logging
    await Assert.That(sourceText).Contains("SchemaInitializationLog");

    // Should accept initConnectionString parameter
    await Assert.That(sourceText).Contains("initConnectionString");
  }

  [Test]
  public async Task Generator_InitCallback_ResolvesInitConnectionStringAsync() {
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

    // Should try {Name}-init connection string first (convention-based PgBouncer bypass)
    await Assert.That(sourceText).Contains("-init");
    await Assert.That(sourceText).Contains("GetConnectionString");

    // Should resolve IConfiguration from the service provider
    await Assert.That(sourceText).Contains("IConfiguration");

    // Should handle null config gracefully (GetService, not GetRequiredService)
    await Assert.That(sourceText).Contains("GetService<Microsoft.Extensions.Configuration.IConfiguration>");

    // Should pass initConnStr to EnsureWhizbangDatabaseInitializedAsync
    await Assert.That(sourceText).Contains("EnsureWhizbangDatabaseInitializedAsync");
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
    const string source = """
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

  /// <summary>
  /// Test that perspective DDL includes physical fields marked with [PhysicalField] attribute.
  /// Physical fields should be added as separate columns in the CREATE TABLE statement.
  /// </summary>
  [Test]
  public async Task Generator_SchemaExtensions_IncludesPhysicalFieldsInDDLAsync() {
    // Arrange - Model with [PhysicalField] attributes
    const string source = """
      using System;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record TestEvent : IEvent;

      [PerspectiveStorage(FieldStorageMode.Split)]
      public record EmbeddingModel {
        [StreamId]
        public Guid Id { get; init; }

        [PhysicalField(Indexed = true)]
        public Guid? ActivityId { get; init; }

        [PhysicalField(Indexed = true)]
        public string? ActivityTreeId { get; init; }

        public string Name { get; init; } = "";
      }

      public class EmbeddingPerspective : IPerspectiveFor<EmbeddingModel, TestEvent> {
        public EmbeddingModel Apply(EmbeddingModel currentData, TestEvent @event) {
          return currentData;
        }
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

    // Should include physical fields as columns in CREATE TABLE
    await Assert.That(sourceText).Contains("activity_id UUID")
      .Because("Physical field ActivityId should be in DDL as activity_id UUID column");
    await Assert.That(sourceText).Contains("activity_tree_id TEXT")
      .Because("Physical field ActivityTreeId should be in DDL as activity_tree_id TEXT column");

    // Should include indexes for physical fields marked with Indexed = true
    await Assert.That(sourceText).Contains("_activity_id")
      .Because("Physical field with Indexed=true should have an index");
    await Assert.That(sourceText).Contains("_activity_tree_id")
      .Because("Physical field with Indexed=true should have an index");
  }

  /// <summary>
  /// Test that physical fields using C# keyword type aliases (string, int, bool, etc.)
  /// map to the correct PostgreSQL column types instead of falling through to TEXT.
  /// Roslyn's FullyQualifiedFormat with UseSpecialTypes outputs keyword aliases for
  /// predefined types, so the type mapper must handle both forms.
  /// </summary>
  [Test]
  public async Task Generator_SchemaExtensions_MapsKeywordTypeAliasesToCorrectPostgresTypesAsync() {
    // Arrange - Model with physical fields using types that Roslyn outputs as keyword aliases
    const string source = """
      using System;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record TestEvent : IEvent;

      [PerspectiveStorage(FieldStorageMode.Split)]
      public record TypeMappingModel {
        [StreamId]
        public Guid Id { get; init; }

        [PhysicalField]
        public int Count { get; init; }

        [PhysicalField]
        public long BigCount { get; init; }

        [PhysicalField]
        public short SmallCount { get; init; }

        [PhysicalField]
        public bool IsActive { get; init; }

        [PhysicalField]
        public string Label { get; init; } = "";

        [PhysicalField]
        public decimal Price { get; init; }

        [PhysicalField]
        public double Score { get; init; }

        [PhysicalField]
        public float Rating { get; init; }
      }

      public class TypeMappingPerspective : IPerspectiveFor<TypeMappingModel, TestEvent> {
        public TypeMappingModel Apply(TypeMappingModel currentData, TestEvent @event) {
          return currentData;
        }
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

    // Each keyword alias type must map to its correct PostgreSQL type, NOT fall through to TEXT
    await Assert.That(sourceText).Contains("count INTEGER")
      .Because("int should map to INTEGER, not TEXT");
    await Assert.That(sourceText).Contains("big_count BIGINT")
      .Because("long should map to BIGINT, not TEXT");
    await Assert.That(sourceText).Contains("small_count SMALLINT")
      .Because("short should map to SMALLINT, not TEXT");
    await Assert.That(sourceText).Contains("is_active BOOLEAN")
      .Because("bool should map to BOOLEAN, not TEXT");
    await Assert.That(sourceText).Contains("label TEXT")
      .Because("string should map to TEXT");
    await Assert.That(sourceText).Contains("price NUMERIC")
      .Because("decimal should map to NUMERIC, not TEXT");
    await Assert.That(sourceText).Contains("score DOUBLE PRECISION")
      .Because("double should map to DOUBLE PRECISION, not TEXT");
    await Assert.That(sourceText).Contains("rating REAL")
      .Because("float should map to REAL, not TEXT");
  }

  /// <summary>
  /// Test that physical fields using fully-qualified .NET type names (System.Guid, System.DateTime, etc.)
  /// continue to map correctly. These are non-keyword types that Roslyn outputs with the global:: prefix.
  /// </summary>
  [Test]
  public async Task Generator_SchemaExtensions_MapsFullyQualifiedTypesToCorrectPostgresTypesAsync() {
    // Arrange - Model with physical fields using types that Roslyn outputs as global::System.X
    const string source = """
      using System;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record TestEvent : IEvent;

      [PerspectiveStorage(FieldStorageMode.Split)]
      public record DateTimeModel {
        [StreamId]
        public Guid Id { get; init; }

        [PhysicalField]
        public Guid CorrelationId { get; init; }

        [PhysicalField]
        public DateTime CreatedAt { get; init; }

        [PhysicalField]
        public DateTimeOffset UpdatedAt { get; init; }

        [PhysicalField]
        public DateOnly BirthDate { get; init; }

        [PhysicalField]
        public TimeOnly StartTime { get; init; }
      }

      public class DateTimePerspective : IPerspectiveFor<DateTimeModel, TestEvent> {
        public DateTimeModel Apply(DateTimeModel currentData, TestEvent @event) {
          return currentData;
        }
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

    await Assert.That(sourceText).Contains("correlation_id UUID")
      .Because("Guid should map to UUID");
    await Assert.That(sourceText).Contains("created_at TIMESTAMPTZ")
      .Because("DateTime should map to TIMESTAMPTZ");
    await Assert.That(sourceText).Contains("updated_at TIMESTAMPTZ")
      .Because("DateTimeOffset should map to TIMESTAMPTZ");
    await Assert.That(sourceText).Contains("birth_date DATE")
      .Because("DateOnly should map to DATE");
    await Assert.That(sourceText).Contains("start_time TIME")
      .Because("TimeOnly should map to TIME");
  }

  /// <summary>
  /// Test that physical fields using explicit global:: qualified types map correctly.
  /// Users may write global::System.Guid or global::System.DateTime directly in their models.
  /// The generator strips the global:: prefix before mapping, so these should work identically.
  /// </summary>
  [Test]
  public async Task Generator_SchemaExtensions_MapsGlobalQualifiedTypesToCorrectPostgresTypesAsync() {
    // Arrange - Model with explicit global:: qualified type names
    const string source = """
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record TestEvent : IEvent;

      [PerspectiveStorage(FieldStorageMode.Split)]
      public record GlobalQualifiedModel {
        [StreamId]
        public global::System.Guid Id { get; init; }

        [PhysicalField]
        public global::System.Guid CorrelationId { get; init; }

        [PhysicalField]
        public global::System.DateTime CreatedAt { get; init; }

        [PhysicalField]
        public global::System.Boolean IsActive { get; init; }

        [PhysicalField]
        public global::System.Int32 Count { get; init; }
      }

      public class GlobalQualifiedPerspective : IPerspectiveFor<GlobalQualifiedModel, TestEvent> {
        public GlobalQualifiedModel Apply(GlobalQualifiedModel currentData, TestEvent @event) {
          return currentData;
        }
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

    await Assert.That(sourceText).Contains("correlation_id UUID")
      .Because("global::System.Guid should map to UUID");
    await Assert.That(sourceText).Contains("created_at TIMESTAMPTZ")
      .Because("global::System.DateTime should map to TIMESTAMPTZ");
    await Assert.That(sourceText).Contains("is_active BOOLEAN")
      .Because("global::System.Boolean should map to BOOLEAN");
    await Assert.That(sourceText).Contains("count INTEGER")
      .Because("global::System.Int32 should map to INTEGER");
  }

  /// <summary>
  /// Test that nullable physical fields with keyword type aliases map correctly.
  /// The nullable suffix (?) is stripped before type mapping, so int? should map the same as int.
  /// </summary>
  [Test]
  public async Task Generator_SchemaExtensions_MapsNullableKeywordTypesToCorrectPostgresTypesAsync() {
    // Arrange - Model with nullable keyword-aliased types
    const string source = """
      using System;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record TestEvent : IEvent;

      [PerspectiveStorage(FieldStorageMode.Split)]
      public record NullableModel {
        [StreamId]
        public Guid Id { get; init; }

        [PhysicalField]
        public int? Count { get; init; }

        [PhysicalField]
        public bool? IsActive { get; init; }

        [PhysicalField]
        public long? BigCount { get; init; }

        [PhysicalField]
        public decimal? Price { get; init; }

        [PhysicalField]
        public DateTime? CreatedAt { get; init; }
      }

      public class NullablePerspective : IPerspectiveFor<NullableModel, TestEvent> {
        public NullableModel Apply(NullableModel currentData, TestEvent @event) {
          return currentData;
        }
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

    await Assert.That(sourceText).Contains("count INTEGER")
      .Because("int? should map to INTEGER after stripping nullable suffix");
    await Assert.That(sourceText).Contains("is_active BOOLEAN")
      .Because("bool? should map to BOOLEAN after stripping nullable suffix");
    await Assert.That(sourceText).Contains("big_count BIGINT")
      .Because("long? should map to BIGINT after stripping nullable suffix");
    await Assert.That(sourceText).Contains("price NUMERIC")
      .Because("decimal? should map to NUMERIC after stripping nullable suffix");
    await Assert.That(sourceText).Contains("created_at TIMESTAMPTZ")
      .Because("DateTime? should map to TIMESTAMPTZ after stripping nullable suffix");
  }

  /// <summary>
  /// Test that perspective DDL includes vector fields marked with [VectorField] attribute.
  /// Vector fields should use pgvector's vector type with specified dimensions.
  /// </summary>
  [Test]
  public async Task Generator_SchemaExtensions_IncludesVectorFieldsInDDLAsync() {
    // Arrange - Model with [VectorField] attribute
    const string source = """
      using System;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record TestEvent : IEvent;

      [PerspectiveStorage(FieldStorageMode.Split)]
      public record EmbeddingModel {
        [StreamId]
        public Guid Id { get; init; }

        [VectorField(1536)]
        public float[]? Embeddings { get; init; }

        public string Name { get; init; } = "";
      }

      public class EmbeddingPerspective : IPerspectiveFor<EmbeddingModel, TestEvent> {
        public EmbeddingModel Apply(EmbeddingModel currentData, TestEvent @event) {
          return currentData;
        }
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

    // Should include vector field with dimensions
    await Assert.That(sourceText).Contains("vector(1536)")
      .Because("Vector field should be in DDL with correct dimensions");

    // Should include vector index with ivfflat method
    await Assert.That(sourceText).Contains("ivfflat")
      .Because("Vector field should have ivfflat index");
  }

  #endregion

  #region Nested Model Class Tests

  /// <summary>
  /// Test that perspectives with nested Model classes generate unique DbSet property names.
  /// Nested Model classes like "ActiveJobTemplate.Model" and "TaskItem.Model" should generate
  /// "ActiveJobTemplateModels" and "TaskItemModels", not duplicate "Models".
  /// This is the fix for CS0102: duplicate DbSet property names.
  /// </summary>
  [Test]
  public async Task Generator_WithNestedModelClasses_GeneratesUniqueDbSetNamesAsync() {
    // Arrange - Two perspectives with nested Model classes (the bug scenario)
    const string source = """
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using System.Threading;
      using System.Threading.Tasks;

      namespace TestApp;

      // First nested Model pattern
      public static class ActiveJobTemplate {
        public record Model {
          public string Id { get; init; } = "";
          public string Name { get; init; } = "";
        }
      }

      // Second nested Model pattern
      public static class TaskItem {
        public record Model {
          public string Id { get; init; } = "";
          public string Description { get; init; } = "";
        }
      }

      // Event for perspectives
      public record JobEvent : IEvent;

      // Perspective for ActiveJobTemplate.Model
      public class ActiveJobTemplatePerspective : IPerspectiveFor<ActiveJobTemplate.Model> {
        private readonly IPerspectiveStore<ActiveJobTemplate.Model> _store;
        public ActiveJobTemplatePerspective(IPerspectiveStore<ActiveJobTemplate.Model> store) => _store = store;
        public string StreamId { get; } = "job";
        public Task ApplyAsync(MessageEnvelope<JobEvent> envelope, CancellationToken ct) => Task.CompletedTask;
      }

      // Perspective for TaskItem.Model
      public class TaskItemPerspective : IPerspectiveFor<TaskItem.Model> {
        private readonly IPerspectiveStore<TaskItem.Model> _store;
        public TaskItemPerspective(IPerspectiveStore<TaskItem.Model> store) => _store = store;
        public string StreamId { get; } = "task";
        public Task ApplyAsync(MessageEnvelope<JobEvent> envelope, CancellationToken ct) => Task.CompletedTask;
      }

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

    // Should have UNIQUE DbSet property names for nested Model classes
    // Bug fix: "Model" should become "ActiveJobTemplateModels" and "TaskItemModels"
    // not just "Models" for both (which causes CS0102 duplicate error)
    await Assert.That(sourceText).Contains("ActiveJobTemplateModels");
    await Assert.That(sourceText).Contains("TaskItemModels");

    // Should NOT have duplicate "Models" property
    // Count occurrences of " Models " (with spaces to avoid false positives)
    var modelsCount = sourceText.Split("public DbSet").Length - 1;
    await Assert.That(modelsCount).IsEqualTo(2); // Two unique DbSet properties
  }

  /// <summary>
  /// Test that perspectives with nested Model classes generate correct table names.
  /// Nested Model classes should have table names that include the parent type.
  /// E.g., "wh_per_active_job_template" (Model suffix stripped) not just "wh_per_model"
  /// </summary>
  [Test]
  public async Task Generator_WithNestedModelClasses_GeneratesCorrectTableNamesAsync() {
    // Arrange - Same scenario as above
    const string source = """
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using System.Threading;
      using System.Threading.Tasks;

      namespace TestApp;

      public static class ActiveJobTemplate {
        public record Model {
          public string Id { get; init; } = "";
        }
      }

      public record JobEvent : IEvent;

      public class ActiveJobTemplatePerspective : IPerspectiveFor<ActiveJobTemplate.Model> {
        private readonly IPerspectiveStore<ActiveJobTemplate.Model> _store;
        public ActiveJobTemplatePerspective(IPerspectiveStore<ActiveJobTemplate.Model> store) => _store = store;
        public string StreamId { get; } = "job";
        public Task ApplyAsync(MessageEnvelope<JobEvent> envelope, CancellationToken ct) => Task.CompletedTask;
      }

      [WhizbangDbContext]
      public partial class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert
    var schemaExtensions = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("SchemaExtensions"));
    await Assert.That(schemaExtensions).IsNotNull();

    var sourceText = schemaExtensions!.SourceText.ToString();

    // Table name should include parent type (nested class scenario)
    // "ActiveJobTemplate.Model" → base name "active_job_template_model" → "Model" suffix stripped
    // → final table name: "wh_per_active_job_template"
    await Assert.That(sourceText).Contains("wh_per_active_job_template");

    // Should NOT have just "wh_per_model" (which would be the bug)
    // We can verify indirectly by ensuring the parent name is included
    await Assert.That(sourceText).Contains("active_job_template");
  }

  #endregion

  #region Nested Perspective Class Tests

  /// <summary>
  /// Test that perspectives nested inside static classes are discovered and registered.
  /// JDNext pattern: static class contains both Model and Projection classes.
  /// The Projection class implements IPerspectiveFor and should be discovered.
  /// </summary>
  /// <remarks>
  /// Bug report: Nested perspective classes like ActiveSessions.Projection were not being
  /// discovered, causing ILensQuery&lt;TModel&gt; to not be registered in DI.
  /// </remarks>
  [Test]
  public async Task Generator_WithNestedPerspectiveClass_DiscoversPerspectiveAsync() {
    // Arrange - JDNext pattern: static class with nested Model and Projection
    const string source = """
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      // Event type
      public record SessionStarted : IEvent;
      public record SessionEnded : IEvent;

      // JDNext pattern: static class contains both Model and Projection
      public static class ActiveSessions {
        public class ActiveSessionsModel {
          public string Id { get; init; } = "";
          public string SessionName { get; init; } = "";
        }

        // Nested perspective class - THIS is the pattern that was failing
        public class Projection : IPerspectiveFor<ActiveSessionsModel, SessionStarted, SessionEnded> {
          public ActiveSessionsModel Apply(ActiveSessionsModel current, SessionStarted e) => current;
          public ActiveSessionsModel Apply(ActiveSessionsModel current, SessionEnded e) => current;
        }
      }

      [WhizbangDbContext]
      public partial class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert - The perspective should be discovered
    var diagnostics = result.Diagnostics
        .Where(d => d.Id == "EFCORE104" || d.Id == "EFCORE105")
        .ToList();

    // EFCORE104 reports count of discovered perspectives
    var countDiag = diagnostics.FirstOrDefault(d => d.Id == "EFCORE104");
    await Assert.That(countDiag).IsNotNull();
    await Assert.That(countDiag!.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("1 perspective");

    // EFCORE105 reports each discovered perspective
    var perspectiveDiag = diagnostics.FirstOrDefault(d => d.Id == "EFCORE105");
    await Assert.That(perspectiveDiag).IsNotNull();
    await Assert.That(perspectiveDiag!.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("ActiveSessions");
  }

  /// <summary>
  /// Test that nested perspective classes result in ILensQuery registration.
  /// The generated code should include registration for ILensQuery&lt;TModel&gt;.
  /// </summary>
  [Test]
  public async Task Generator_WithNestedPerspectiveClass_GeneratesLensQueryRegistrationAsync() {
    // Arrange - Same JDNext pattern
    const string source = """
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record ChatMessage : IEvent;

      public static class ActiveChatSummary {
        public class ActiveChatSummaryModel {
          public string Id { get; init; } = "";
          public int MessageCount { get; init; }
        }

        public class Projection : IPerspectiveFor<ActiveChatSummaryModel, ChatMessage> {
          public ActiveChatSummaryModel Apply(ActiveChatSummaryModel current, ChatMessage e) => current;
        }
      }

      [WhizbangDbContext]
      public partial class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert - Check that registration metadata is generated
    var registrationFile = result.GeneratedSources
        .FirstOrDefault(s => s.HintName.Contains("EFCoreModelRegistration"));
    await Assert.That(registrationFile).IsNotNull();

    var sourceText = registrationFile!.SourceText.ToString();

    // Should register ILensQuery for the nested model
    await Assert.That(sourceText).Contains("ILensQuery<");
    await Assert.That(sourceText).Contains("ActiveChatSummaryModel");
  }

  /// <summary>
  /// Debug test: Output the full generated registration code for nested perspective classes.
  /// This helps verify that ILensQuery registration is properly generated.
  /// </summary>
  [Test]
  public async Task Generator_WithNestedPerspectiveClass_GeneratesCorrectRegistrationCodeAsync() {
    // Arrange - JDNext pattern
    const string source = """
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record SessionStarted : IEvent;

      public static class ActiveSessions {
        public class ActiveSessionsModel {
          public string Id { get; init; } = "";
        }

        public class Projection : IPerspectiveFor<ActiveSessionsModel, SessionStarted> {
          public ActiveSessionsModel Apply(ActiveSessionsModel current, SessionStarted e) => current;
        }
      }

      [WhizbangDbContext]
      public partial class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert - Check registration code
    var registrationFile = result.GeneratedSources
        .FirstOrDefault(s => s.HintName.Contains("EFCoreModelRegistration"));
    await Assert.That(registrationFile).IsNotNull();

    var sourceText = registrationFile!.SourceText.ToString();

    // The registration code should include:
    // 1. ILensQuery<ActiveSessions.ActiveSessionsModel> registration
    // 2. IPerspectiveStore<ActiveSessions.ActiveSessionsModel> registration
    // 3. Table name includes containing type (wh_per_active_sessions_active_sessions_model)

    await Assert.That(sourceText).Contains("ILensQuery<global::TestApp.ActiveSessions.ActiveSessionsModel>");
    await Assert.That(sourceText).Contains("IPerspectiveStore<global::TestApp.ActiveSessions.ActiveSessionsModel>");
    // Nested model: ActiveSessions.ActiveSessionsModel -> table: wh_per_active_sessions_active_sessions_model
    await Assert.That(sourceText).Contains("wh_per_");
    await Assert.That(sourceText).Contains("active_sessions");
  }

  /// <summary>
  /// Test that multiple nested perspective classes in different static containers are all discovered.
  /// </summary>
  [Test]
  public async Task Generator_WithMultipleNestedPerspectiveClasses_DiscoversAllAsync() {
    // Arrange - Multiple static classes with nested perspectives
    const string source = """
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record UserEvent : IEvent;
      public record OrderEvent : IEvent;

      public static class UserSessions {
        public class Model {
          public string Id { get; init; } = "";
        }
        public class Projection : IPerspectiveFor<Model, UserEvent> {
          public Model Apply(Model current, UserEvent e) => current;
        }
      }

      public static class OrderSummary {
        public class Model {
          public string Id { get; init; } = "";
        }
        public class Projection : IPerspectiveFor<Model, OrderEvent> {
          public Model Apply(Model current, OrderEvent e) => current;
        }
      }

      [WhizbangDbContext]
      public partial class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert - Both perspectives should be discovered
    var countDiag = result.Diagnostics.FirstOrDefault(d => d.Id == "EFCORE104");
    await Assert.That(countDiag).IsNotNull();
    await Assert.That(countDiag!.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("2 perspective");

    // DbContext partial should have DbSets for both
    var partialClass = result.GeneratedSources
        .FirstOrDefault(s => s.HintName.Contains("TestDbContext.Generated"));
    await Assert.That(partialClass).IsNotNull();

    var sourceText = partialClass!.SourceText.ToString();
    // Should have unique DbSet names based on containing type
    await Assert.That(sourceText).Contains("UserSessionsModels");
    await Assert.That(sourceText).Contains("OrderSummaryModels");
  }

  #endregion

  #region Step 5 - Register Perspective Associations Tests

  /// <summary>
  /// Test that schema extensions include Step 5 comment indicating perspective association registration.
  /// Step 5 registers perspective→event type mappings in wh_message_associations table.
  /// This enables process_work_batch Phase 4.6 to create perspective events.
  /// </summary>
  [Test]
  public async Task Generator_SchemaExtensions_IncludesStep5_RegisterPerspectiveAssociationsAsync() {
    // Arrange - source with DbContext and a perspective
    const string source = """
      using System.Threading;
      using System.Threading.Tasks;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record TestEvent : IEvent;
      public record TestModel { public string Id { get; init; } = ""; }

      public class TestPerspective : IPerspectiveFor<TestModel, TestEvent> {
        public TestModel Apply(TestModel current, TestEvent e) => current;
      }

      [WhizbangDbContext]
      public partial class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);
    var schemaExtensions = result.GeneratedSources
      .FirstOrDefault(s => s.HintName.Contains("SchemaExtensions"));

    // Assert - Step 5 should be present and call RegisterPerspectiveAssociationsAsync
    await Assert.That(schemaExtensions).IsNotNull();
    var sourceText = schemaExtensions!.SourceText.ToString();

    // Should have Step 5 comment
    await Assert.That(sourceText).Contains("Step 5");

    // Should call RegisterPerspectiveAssociationsAsync
    await Assert.That(sourceText).Contains("RegisterPerspectiveAssociationsAsync");
  }

  /// <summary>
  /// Test that schema extensions contain all 5 initialization steps in order.
  /// Steps: 1) Core infrastructure, 2) Perspective tables, 3) Constraints, 4) Migrations, 5) Perspective associations.
  /// </summary>
  [Test]
  public async Task Generator_SchemaExtensions_ContainsAllFiveStepsAsync() {
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
    var schemaExtensions = result.GeneratedSources
      .FirstOrDefault(s => s.HintName.Contains("SchemaExtensions"));

    // Assert - All 5 steps should be present
    await Assert.That(schemaExtensions).IsNotNull();
    var code = schemaExtensions!.SourceText.ToString();

    // Verify all steps exist
    await Assert.That(code).Contains("Step 1"); // Core infrastructure
    await Assert.That(code).Contains("Step 2"); // Perspective tables
    await Assert.That(code).Contains("Step 3"); // Constraints
    await Assert.That(code).Contains("Step 4"); // Migrations
    await Assert.That(code).Contains("Step 5"); // Perspective associations
  }

  /// <summary>
  /// Test that Step 5 calls RegisterPerspectiveAssociationsAsync with correct parameters.
  /// The generated code uses extension method pattern with schema and assembly name parameters.
  /// NOTE: The PERSPECTIVE_BOILERPLATE must include a perspective implementing IPerspectiveFor
  /// for this test to verify Step 5 generates the RegisterPerspectiveAssociationsAsync call.
  /// </summary>
  [Test]
  public async Task Generator_SchemaExtensions_Step5UsesExtensionMethodPatternAsync() {
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
    var schemaExtensions = result.GeneratedSources
      .FirstOrDefault(s => s.HintName.Contains("SchemaExtensions"));

    // Assert - Should use extension method pattern
    await Assert.That(schemaExtensions).IsNotNull();
    var sourceText = schemaExtensions!.SourceText.ToString();

    // The generated code should call dbContext.RegisterPerspectiveAssociationsAsync(...)
    // This only happens when perspectives are detected and matched to the DbContext
    await Assert.That(sourceText).Contains("await dbContext.RegisterPerspectiveAssociationsAsync(");
  }

  /// <summary>
  /// Test that Step 5 is called AFTER Step 4 (migrations).
  /// The order matters because associations should only be registered after the
  /// wh_message_associations table and register_message_associations function are created.
  /// </summary>
  [Test]
  public async Task Generator_SchemaExtensions_Step5ComesAfterStep4Async() {
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
    var schemaExtensions = result.GeneratedSources
      .FirstOrDefault(s => s.HintName.Contains("SchemaExtensions"));

    // Assert - Step 5 must come after Step 4
    await Assert.That(schemaExtensions).IsNotNull();
    var code = schemaExtensions!.SourceText.ToString();

    // Find positions
    var step4Index = code.IndexOf("Step 4", StringComparison.Ordinal);
    var step5Index = code.IndexOf("Step 5", StringComparison.Ordinal);

    await Assert.That(step4Index).IsGreaterThan(-1);
    await Assert.That(step5Index).IsGreaterThan(-1);
    await Assert.That(step5Index).IsGreaterThan(step4Index);
  }

  #endregion

  #region Multi-Model ILensQuery Auto-Detection Tests

  // Multi-model boilerplate with two perspectives and a class injecting ILensQuery<T1, T2>
  private const string MULTI_LENS_BOILERPLATE = """
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Core.Lenses;

    // Events
    public record TestEvent : IEvent;
    public record TestEvent2 : IEvent;

    // Models
    public record ModelA {
      public string Id { get; init; } = "";
    }

    public record ModelB {
      public string Id { get; init; } = "";
    }

    public record ModelC {
      public string Id { get; init; } = "";
    }

    // Perspectives
    public class PerspectiveA : IPerspectiveFor<ModelA, TestEvent> {
      private readonly IPerspectiveStore<ModelA> _store;
      public PerspectiveA(IPerspectiveStore<ModelA> store) => _store = store;
      public ModelA Apply(ModelA currentData, TestEvent eventData) => currentData with { Id = "a" };
    }

    public class PerspectiveB : IPerspectiveFor<ModelB, TestEvent2> {
      private readonly IPerspectiveStore<ModelB> _store;
      public PerspectiveB(IPerspectiveStore<ModelB> store) => _store = store;
      public ModelB Apply(ModelB currentData, TestEvent2 eventData) => currentData with { Id = "b" };
    }

    public class PerspectiveC : IPerspectiveFor<ModelC, TestEvent> {
      private readonly IPerspectiveStore<ModelC> _store;
      public PerspectiveC(IPerspectiveStore<ModelC> store) => _store = store;
      public ModelC Apply(ModelC currentData, TestEvent eventData) => currentData with { Id = "c" };
    }
    """;

  /// <summary>
  /// Test that a constructor parameter typed as ILensQuery&lt;T1, T2&gt; is auto-detected
  /// and generates a transient registration in the registration metadata.
  /// </summary>
  [Test]
  public async Task Generator_WithMultiModelLensQueryConstructorParam_GeneratesRegistrationAsync() {
    // Arrange
    var source = $$"""
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;
      using Whizbang.Core.Lenses;

      namespace TestApp;

      {{MULTI_LENS_BOILERPLATE}}

      // Repository that uses multi-model ILensQuery
      public class MyRepository {
        public MyRepository(ILensQuery<ModelA, ModelB> query) { }
      }

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

    // Should generate transient registration for ILensQuery<ModelA, ModelB>
    await Assert.That(sourceText).Contains("services.AddTransient<Whizbang.Core.Lenses.ILensQuery<global::TestApp.ModelA, global::TestApp.ModelB>>");
    await Assert.That(sourceText).Contains("EFCorePostgresLensQuery<global::TestApp.ModelA, global::TestApp.ModelB>");

    // Should include table name mappings for both models
    await Assert.That(sourceText).Contains("[typeof(global::TestApp.ModelA)] = \"wh_per_model_a\"");
    await Assert.That(sourceText).Contains("[typeof(global::TestApp.ModelB)] = \"wh_per_model_b\"");

    // Should pass scopeContextAccessor and whizbangOptions to constructor
    // (constructor requires 4 params: context, tableNames, scopeContextAccessor, whizbangOptions)
    await Assert.That(sourceText).Contains("scopeContextAccessor")
      .Because("Multi-model LensQuery constructor requires IScopeContextAccessor parameter");
    await Assert.That(sourceText).Contains("whizbangOptions")
      .Because("Multi-model LensQuery constructor requires IOptions<WhizbangCoreOptions> parameter");

    // Should have auto-detected comment
    await Assert.That(sourceText).Contains("Auto-detected: ILensQuery<TestApp.ModelA, TestApp.ModelB>");
  }

  /// <summary>
  /// Test that when a model type in ILensQuery&lt;T1, T2&gt; has no corresponding perspective,
  /// WHIZ401 warning is reported and no registration is generated.
  /// </summary>
  [Test]
  public async Task Generator_WithMultiModelLensQuery_UnknownModel_ReportsWHIZ401Async() {
    // Arrange - UnknownModel has no perspective
    var source = $$"""
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Core.Lenses;

      namespace TestApp;

      public record TestEvent : IEvent;
      public record ModelA { public string Id { get; init; } = ""; }
      public record UnknownModel { public string Id { get; init; } = ""; }

      public class PerspectiveA : IPerspectiveFor<ModelA, TestEvent> {
        private readonly IPerspectiveStore<ModelA> _store;
        public PerspectiveA(IPerspectiveStore<ModelA> store) => _store = store;
        public ModelA Apply(ModelA currentData, TestEvent eventData) => currentData with { Id = "a" };
      }

      // No perspective for UnknownModel!

      public class MyRepository {
        public MyRepository(ILensQuery<ModelA, UnknownModel> query) { }
      }

      [WhizbangDbContext]
      public class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert - should report WHIZ401 warning
    var whiz401 = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ401");
    await Assert.That(whiz401).IsNotNull();
    await Assert.That(whiz401!.Severity).IsEqualTo(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);

    // Should NOT generate registration for the invalid combo
    var registration = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("EFCoreModelRegistration"));
    await Assert.That(registration).IsNotNull();
    var sourceText = registration!.SourceText.ToString();
    await Assert.That(sourceText).DoesNotContain("ILensQuery<global::TestApp.ModelA, global::TestApp.UnknownModel>");
  }

  /// <summary>
  /// Test that when two different classes inject the same ILensQuery&lt;T1, T2&gt;,
  /// only one registration is generated (deduplication).
  /// </summary>
  [Test]
  public async Task Generator_WithMultiModelLensQuery_DuplicateUsage_RegistersOnceAsync() {
    // Arrange
    var source = $$"""
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;
      using Whizbang.Core.Lenses;

      namespace TestApp;

      {{MULTI_LENS_BOILERPLATE}}

      // Two different classes both inject ILensQuery<ModelA, ModelB>
      public class RepositoryOne {
        public RepositoryOne(ILensQuery<ModelA, ModelB> query) { }
      }

      public class RepositoryTwo {
        public RepositoryTwo(ILensQuery<ModelA, ModelB> query) { }
      }

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

    // Should contain ILensQuery<ModelA, ModelB> registration
    await Assert.That(sourceText).Contains("ILensQuery<global::TestApp.ModelA, global::TestApp.ModelB>");

    // Count occurrences - should appear exactly once as a registration target
    const string needle = "services.AddTransient<Whizbang.Core.Lenses.ILensQuery<global::TestApp.ModelA, global::TestApp.ModelB>>";
    var registrationCount = 0;
    var searchIndex = 0;
    while ((searchIndex = sourceText.IndexOf(needle, searchIndex, StringComparison.Ordinal)) != -1) {
      registrationCount++;
      searchIndex += needle.Length;
    }
    await Assert.That(registrationCount).IsEqualTo(1);
  }

  /// <summary>
  /// Test that ILensQuery&lt;T1, T2, T3&gt; (arity 3) is auto-detected and generates registration.
  /// </summary>
  [Test]
  public async Task Generator_WithMultiModelLensQuery_ThreeModels_GeneratesRegistrationAsync() {
    // Arrange
    var source = $$"""
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;
      using Whizbang.Core.Lenses;

      namespace TestApp;

      {{MULTI_LENS_BOILERPLATE}}

      // Repository that uses three-model ILensQuery
      public class TripleRepository {
        public TripleRepository(ILensQuery<ModelA, ModelB, ModelC> query) { }
      }

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

    // Should generate registration for three-model ILensQuery
    await Assert.That(sourceText).Contains("ILensQuery<global::TestApp.ModelA, global::TestApp.ModelB, global::TestApp.ModelC>");
    await Assert.That(sourceText).Contains("EFCorePostgresLensQuery<global::TestApp.ModelA, global::TestApp.ModelB, global::TestApp.ModelC>");

    // Should pass scopeContextAccessor and whizbangOptions to constructor
    await Assert.That(sourceText).Contains("scopeContextAccessor")
      .Because("Multi-model LensQuery constructor requires IScopeContextAccessor parameter");
    await Assert.That(sourceText).Contains("whizbangOptions")
      .Because("Multi-model LensQuery constructor requires IOptions<WhizbangCoreOptions> parameter");

    // Should include all three table name mappings
    await Assert.That(sourceText).Contains("[typeof(global::TestApp.ModelA)] = \"wh_per_model_a\"");
    await Assert.That(sourceText).Contains("[typeof(global::TestApp.ModelB)] = \"wh_per_model_b\"");
    await Assert.That(sourceText).Contains("[typeof(global::TestApp.ModelC)] = \"wh_per_model_c\"");
  }

  /// <summary>
  /// Test that auto-detected multi-model ILensQuery generates correct table names
  /// matching the wh_per_ + snake_case convention used by perspectives.
  /// </summary>
  [Test]
  public async Task Generator_WithMultiModelLensQuery_GeneratesCorrectTableNamesAsync() {
    // Arrange
    var source = $$"""
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;
      using Whizbang.Core.Lenses;

      namespace TestApp;

      {{MULTI_LENS_BOILERPLATE}}

      public class MyRepo {
        public MyRepo(ILensQuery<ModelA, ModelB> query) { }
      }

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

    // Table names should use wh_per_ prefix with snake_case model names
    // These should match what the perspective pipeline generates
    await Assert.That(sourceText).Contains("\"wh_per_model_a\"");
    await Assert.That(sourceText).Contains("\"wh_per_model_b\"");

    // The table names in multi-model registration should be in a Dictionary
    await Assert.That(sourceText).Contains("System.Collections.Generic.Dictionary<System.Type, string>");
  }

  #endregion
}
