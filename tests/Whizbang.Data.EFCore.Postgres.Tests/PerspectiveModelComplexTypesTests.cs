using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for complex property types in perspective models.
/// Verifies that List&lt;AttributeEntry&gt;, nullable types, enums, and other complex types
/// work correctly with EF Core 10's ComplexProperty().ToJson() pattern.
/// </summary>
/// <remarks>
/// EF Core 10's ComplexProperty().ToJson() does NOT support Dictionary&lt;K,V&gt; types - it throws
/// NullReferenceException in CreateReadJsonPropertyValueExpression.
/// Use List&lt;AttributeEntry&gt; or List&lt;KeyValuePair&lt;string, string&gt;&gt; for key-value metadata instead.
/// </remarks>
[Category("Integration")]
[NotInParallel("PostgreSQL")]
public class PerspectiveModelComplexTypesTests : IAsyncDisposable {
  private static readonly Uuid7IdProvider _idProvider = new();

  /// <summary>
  /// Creates a new Guid using the ID provider. Returns Guid directly (not TrackedGuid)
  /// for EF Core query compatibility.
  /// </summary>
  private static Guid _newGuid() => (Guid)_idProvider.NewGuid();

  static PerspectiveModelComplexTypesTests() {
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
  }

  private string? _testDatabaseName;
  private NpgsqlDataSource? _dataSource;
  private ComplexTypesDbContext? _context;
  private string _connectionString = null!;

  /// <summary>
  /// Status enum for testing enum serialization in JSON columns.
  /// </summary>
  public enum TenantStatus {
    Pending,
    Active,
    Suspended,
    Deleted
  }

  /// <summary>
  /// Attribute entry for storing key-value pairs in perspective models.
  /// Use this instead of Dictionary&lt;string, string&gt; which is NOT supported by EF Core's ToJson().
  /// </summary>
  public class AttributeEntry {
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
  }

  /// <summary>
  /// Test model showing the correct approach for key-value metadata in perspective models.
  /// Uses List&lt;AttributeEntry&gt; instead of Dictionary&lt;string, string&gt;.
  /// </summary>
  /// <remarks>
  /// EF Core 10's ComplexProperty().ToJson() does NOT support Dictionary&lt;K,V&gt; properties.
  /// It throws NullReferenceException in CreateReadJsonPropertyValueExpression.
  /// Use List&lt;AttributeEntry&gt; or List&lt;KeyValuePair&lt;string, string&gt;&gt; instead.
  /// </remarks>
  public class TenantModel {
    [StreamId]
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Use List of AttributeEntry instead of Dictionary for key-value metadata.
    /// This is fully supported by EF Core's ComplexProperty().ToJson().
    /// </summary>
    public List<AttributeEntry> Attributes { get; set; } = [];

    public DateTimeOffset? AuthorizedOn { get; set; }
    public TenantStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
  }

  /// <summary>
  /// Test model with multiple complex property types.
  /// </summary>
  public class ComplexModel {
    [StreamId]
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>List of AttributeEntry for key-value metadata (NOT Dictionary).</summary>
    public List<AttributeEntry> StringMetadata { get; set; } = [];

    /// <summary>Nullable Guid for optional references.</summary>
    public Guid? OptionalReference { get; set; }

    /// <summary>Nullable DateTimeOffset for optional timestamps.</summary>
    public DateTimeOffset? OptionalTimestamp { get; set; }

    /// <summary>Nullable int for optional counts.</summary>
    public int? OptionalCount { get; set; }

    /// <summary>List of Guids for testing collection serialization.</summary>
    public List<Guid> RelatedIds { get; set; } = [];

    /// <summary>List of nullable Guids for testing complex collections.</summary>
    public List<Guid?> OptionalRelatedIds { get; set; } = [];

    /// <summary>Enum property for status tracking.</summary>
    public TenantStatus Status { get; set; }
  }

  /// <summary>
  /// DbContext using ComplexProperty().ToJson() for JSONB columns.
  /// Uses List&lt;AttributeEntry&gt; instead of Dictionary&lt;K,V&gt; which is NOT supported.
  /// </summary>
  private sealed class ComplexTypesDbContext(DbContextOptions<PerspectiveModelComplexTypesTests.ComplexTypesDbContext> options) : DbContext(options) {
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) {
      // Enable TrackedGuid support for queries using Uuid7IdProvider
      configurationBuilder.UseTrackedGuidConversion();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);

      // TenantModel configuration - tests List<AttributeEntry> for key-value metadata
      modelBuilder.Entity<PerspectiveRow<TenantModel>>(entity => {
        entity.ToTable("wh_per_tenant_test");
        entity.HasKey(e => e.Id);

        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        entity.Property(e => e.Version).HasColumnName("version");

        // EF Core 10 ComplexProperty().ToJson() for full LINQ support
        // Using List<AttributeEntry> instead of Dictionary<string, string>
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
        entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
        entity.ComplexProperty(e => e.Scope, s => s.ToJson("scope"));
      });

      // ComplexModel configuration - tests various complex types
      modelBuilder.Entity<PerspectiveRow<ComplexModel>>(entity => {
        entity.ToTable("wh_per_complex_test");
        entity.HasKey(e => e.Id);

        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        entity.Property(e => e.Version).HasColumnName("version");

        // EF Core 10 ComplexProperty().ToJson() for full LINQ support
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
        entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
        entity.ComplexProperty(e => e.Scope, s => s.ToJson("scope"));
      });
    }
  }

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();

    _testDatabaseName = $"complex_types_test_{Guid.NewGuid():N}";

    await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await adminConnection.OpenAsync();
    await adminConnection.ExecuteAsync($"CREATE DATABASE {_testDatabaseName}");

    var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _testDatabaseName,
      Timezone = "UTC",
      IncludeErrorDetail = true
    };
    _connectionString = builder.ConnectionString;

    var dataSourceBuilder = new NpgsqlDataSourceBuilder(_connectionString);
    dataSourceBuilder.EnableDynamicJson();
    _dataSource = dataSourceBuilder.Build();

    var optionsBuilder = new DbContextOptionsBuilder<ComplexTypesDbContext>();
    optionsBuilder
        .UseNpgsql(_dataSource)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    _context = new ComplexTypesDbContext(optionsBuilder.Options);

    await _initializeSchemaAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_context != null) {
      await _context.DisposeAsync();
      _context = null;
    }

    if (_dataSource != null) {
      await _dataSource.DisposeAsync();
      _dataSource = null;
    }

    if (_testDatabaseName != null) {
      try {
        await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await adminConnection.OpenAsync();

        await adminConnection.ExecuteAsync($@"
          SELECT pg_terminate_backend(pg_stat_activity.pid)
          FROM pg_stat_activity
          WHERE pg_stat_activity.datname = '{_testDatabaseName}'
          AND pid <> pg_backend_pid()");

        await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {_testDatabaseName}");
      } catch {
        // Ignore cleanup errors
      }

      _testDatabaseName = null;
    }
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  private async Task _initializeSchemaAsync() {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();

    await connection.ExecuteAsync("""
      CREATE TABLE IF NOT EXISTS wh_per_tenant_test (
        id UUID PRIMARY KEY,
        data JSONB NOT NULL,
        metadata JSONB NOT NULL,
        scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL,
        updated_at TIMESTAMPTZ NOT NULL,
        version INTEGER NOT NULL
      );

      CREATE TABLE IF NOT EXISTS wh_per_complex_test (
        id UUID PRIMARY KEY,
        data JSONB NOT NULL,
        metadata JSONB NOT NULL,
        scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL,
        updated_at TIMESTAMPTZ NOT NULL,
        version INTEGER NOT NULL
      );
      """);
  }

  // ==================== LIST<ATTRIBUTEENTRY> PROPERTY TESTS ====================

  /// <summary>
  /// Verifies List&lt;AttributeEntry&gt; (key-value metadata) can be saved and retrieved.
  /// This pattern replaces Dictionary&lt;string, string&gt; which is NOT supported by ComplexProperty().ToJson().
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task ListOfAttributeEntry_CanBeSavedAndRetrievedAsync(CancellationToken cancellationToken) {
    // Arrange
    var tenantId = _newGuid();
    var rowId = _newGuid();
    var tenant = new TenantModel {
      Id = rowId,
      TenantId = tenantId,
      Name = "Test Tenant",
      Attributes = [
        new() { Key = "Region", Value = "US-West" },
        new() { Key = "Tier", Value = "Premium" },
        new() { Key = "Feature_BetaAccess", Value = "true" }
      ],
      Status = TenantStatus.Active,
      AuthorizedOn = DateTimeOffset.UtcNow,
      CreatedAt = DateTimeOffset.UtcNow,
      UpdatedAt = DateTimeOffset.UtcNow
    };

    var row = new PerspectiveRow<TenantModel> {
      Id = rowId,
      Data = tenant,
      Metadata = new PerspectiveMetadata {
        EventType = "TenantCreated",
        EventId = Guid.NewGuid().ToString(),
        Timestamp = DateTime.UtcNow
      },
      Scope = new PerspectiveScope(),
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      Version = 1
    };

    // Act - Save
    _context!.Set<PerspectiveRow<TenantModel>>().Add(row);
    await _context.SaveChangesAsync(cancellationToken);

    // Clear tracker to force fresh read from database
    _context.ChangeTracker.Clear();

    // Act - Retrieve
    var retrieved = await _context.Set<PerspectiveRow<TenantModel>>()
        .FirstOrDefaultAsync(r => r.Id == rowId, cancellationToken);

    // Assert
    await Assert.That(retrieved).IsNotNull();
    await Assert.That(retrieved!.Data.Name).IsEqualTo("Test Tenant");
    await Assert.That(retrieved.Data.Attributes).IsNotNull();
    await Assert.That(retrieved.Data.Attributes.Count).IsEqualTo(3);
    await Assert.That(retrieved.Data.Attributes.First(a => a.Key == "Region").Value).IsEqualTo("US-West");
    await Assert.That(retrieved.Data.Attributes.First(a => a.Key == "Tier").Value).IsEqualTo("Premium");
    await Assert.That(retrieved.Data.Attributes.First(a => a.Key == "Feature_BetaAccess").Value).IsEqualTo("true");
  }

  /// <summary>
  /// Verifies empty List&lt;AttributeEntry&gt; is handled correctly.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task EmptyAttributeList_CanBeSavedAndRetrievedAsync(CancellationToken cancellationToken) {
    // Arrange
    var rowId = _newGuid();
    var tenant = new TenantModel {
      Id = rowId,
      TenantId = _newGuid(),
      Name = "Empty Attributes Tenant",
      Attributes = [], // Empty list
      Status = TenantStatus.Pending,
      CreatedAt = DateTimeOffset.UtcNow,
      UpdatedAt = DateTimeOffset.UtcNow
    };

    var row = new PerspectiveRow<TenantModel> {
      Id = rowId,
      Data = tenant,
      Metadata = new PerspectiveMetadata {
        EventType = "TenantCreated",
        EventId = Guid.NewGuid().ToString(),
        Timestamp = DateTime.UtcNow
      },
      Scope = new PerspectiveScope(),
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      Version = 1
    };

    // Act
    _context!.Set<PerspectiveRow<TenantModel>>().Add(row);
    await _context.SaveChangesAsync(cancellationToken);
    _context.ChangeTracker.Clear();

    var retrieved = await _context.Set<PerspectiveRow<TenantModel>>()
        .FirstOrDefaultAsync(r => r.Id == rowId, cancellationToken);

    // Assert
    await Assert.That(retrieved).IsNotNull();
    await Assert.That(retrieved!.Data.Attributes).IsNotNull();
    await Assert.That(retrieved.Data.Attributes.Count).IsEqualTo(0);
  }

  // ==================== NULLABLE TYPE TESTS ====================

  /// <summary>
  /// Verifies nullable DateTimeOffset works correctly.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task NullableDateTimeOffset_CanBeSavedAndRetrievedAsync(CancellationToken cancellationToken) {
    // Arrange - with value
    var rowId1 = _newGuid();
    var now = DateTimeOffset.UtcNow;
    var tenant1 = new TenantModel {
      Id = rowId1,
      TenantId = _newGuid(),
      Name = "Authorized Tenant",
      Attributes = [],
      Status = TenantStatus.Active,
      AuthorizedOn = now, // Has value
      CreatedAt = DateTimeOffset.UtcNow,
      UpdatedAt = DateTimeOffset.UtcNow
    };

    // Arrange - without value
    var rowId2 = _newGuid();
    var tenant2 = new TenantModel {
      Id = rowId2,
      TenantId = _newGuid(),
      Name = "Pending Tenant",
      Attributes = [],
      Status = TenantStatus.Pending,
      AuthorizedOn = null, // No value
      CreatedAt = DateTimeOffset.UtcNow,
      UpdatedAt = DateTimeOffset.UtcNow
    };

    var row1 = _createRow(rowId1, tenant1);
    var row2 = _createRow(rowId2, tenant2);

    // Act
    _context!.Set<PerspectiveRow<TenantModel>>().AddRange(row1, row2);
    await _context.SaveChangesAsync(cancellationToken);
    _context.ChangeTracker.Clear();

    var retrieved1 = await _context.Set<PerspectiveRow<TenantModel>>()
        .FirstOrDefaultAsync(r => r.Id == rowId1, cancellationToken);
    var retrieved2 = await _context.Set<PerspectiveRow<TenantModel>>()
        .FirstOrDefaultAsync(r => r.Id == rowId2, cancellationToken);

    // Assert
    await Assert.That(retrieved1!.Data.AuthorizedOn).IsNotNull();
    await Assert.That(retrieved2!.Data.AuthorizedOn).IsNull();
  }

  /// <summary>
  /// Verifies nullable Guid works correctly.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task NullableGuid_CanBeSavedAndRetrievedAsync(CancellationToken cancellationToken) {
    // Arrange
    var rowId1 = _newGuid();
    var referenceId = _newGuid();
    var model1 = new ComplexModel {
      Id = rowId1,
      Name = "With Reference",
      OptionalReference = referenceId,
      Status = TenantStatus.Active
    };

    var rowId2 = _newGuid();
    var model2 = new ComplexModel {
      Id = rowId2,
      Name = "Without Reference",
      OptionalReference = null,
      Status = TenantStatus.Pending
    };

    var row1 = _createComplexRow(rowId1, model1);
    var row2 = _createComplexRow(rowId2, model2);

    // Act
    _context!.Set<PerspectiveRow<ComplexModel>>().AddRange(row1, row2);
    await _context.SaveChangesAsync(cancellationToken);
    _context.ChangeTracker.Clear();

    var retrieved1 = await _context.Set<PerspectiveRow<ComplexModel>>()
        .FirstOrDefaultAsync(r => r.Id == rowId1, cancellationToken);
    var retrieved2 = await _context.Set<PerspectiveRow<ComplexModel>>()
        .FirstOrDefaultAsync(r => r.Id == rowId2, cancellationToken);

    // Assert
    await Assert.That(retrieved1!.Data.OptionalReference).IsEqualTo(referenceId);
    await Assert.That(retrieved2!.Data.OptionalReference).IsNull();
  }

  /// <summary>
  /// Verifies List&lt;Guid?&gt; (nullable Guid collection) works correctly.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task ListOfNullableGuid_CanBeSavedAndRetrievedAsync(CancellationToken cancellationToken) {
    // Arrange
    var rowId = _newGuid();
    var guid1 = _newGuid();
    var guid2 = _newGuid();
    var model = new ComplexModel {
      Id = rowId,
      Name = "With Nullable Guid List",
      OptionalRelatedIds = [guid1, null, guid2, null],
      Status = TenantStatus.Active
    };

    var row = _createComplexRow(rowId, model);

    // Act
    _context!.Set<PerspectiveRow<ComplexModel>>().Add(row);
    await _context.SaveChangesAsync(cancellationToken);
    _context.ChangeTracker.Clear();

    var retrieved = await _context.Set<PerspectiveRow<ComplexModel>>()
        .FirstOrDefaultAsync(r => r.Id == rowId, cancellationToken);

    // Assert
    await Assert.That(retrieved).IsNotNull();
    await Assert.That(retrieved!.Data.OptionalRelatedIds.Count).IsEqualTo(4);
    await Assert.That(retrieved.Data.OptionalRelatedIds[0]).IsEqualTo(guid1);
    await Assert.That(retrieved.Data.OptionalRelatedIds[1]).IsNull();
    await Assert.That(retrieved.Data.OptionalRelatedIds[2]).IsEqualTo(guid2);
    await Assert.That(retrieved.Data.OptionalRelatedIds[3]).IsNull();
  }

  // ==================== ENUM TESTS ====================

  /// <summary>
  /// Verifies enum properties are serialized and deserialized correctly.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task EnumProperty_CanBeSavedAndRetrievedAsync(CancellationToken cancellationToken) {
    // Arrange - test all enum values
    var rows = new List<PerspectiveRow<TenantModel>>();
    var rowIds = new Dictionary<TenantStatus, Guid>();

    foreach (var status in Enum.GetValues<TenantStatus>()) {
      var rowId = _newGuid();
      rowIds[status] = rowId;

      var tenant = new TenantModel {
        Id = rowId,
        TenantId = _newGuid(),
        Name = $"Tenant with {status} status",
        Attributes = [],
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
      };

      rows.Add(_createRow(rowId, tenant));
    }

    // Act
    _context!.Set<PerspectiveRow<TenantModel>>().AddRange(rows);
    await _context.SaveChangesAsync(cancellationToken);
    _context.ChangeTracker.Clear();

    // Assert - verify each status
    foreach (var status in Enum.GetValues<TenantStatus>()) {
      var retrieved = await _context.Set<PerspectiveRow<TenantModel>>()
          .FirstOrDefaultAsync(r => r.Id == rowIds[status], cancellationToken);

      await Assert.That(retrieved).IsNotNull();
      await Assert.That(retrieved!.Data.Status).IsEqualTo(status);
    }
  }

  // ==================== MULTIPLE COMPLEX TYPES TEST ====================

  /// <summary>
  /// Verifies a model with multiple complex type properties works correctly.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task MultipleComplexTypes_CanBeSavedAndRetrievedAsync(CancellationToken cancellationToken) {
    // Arrange
    var rowId = _newGuid();
    var now = DateTimeOffset.UtcNow;
    var guid1 = _newGuid();
    var guid2 = _newGuid();

    var model = new ComplexModel {
      Id = rowId,
      Name = "Complex Model",
      StringMetadata = [
        new() { Key = "Key1", Value = "Value1" },
        new() { Key = "Key2", Value = "Value2" }
      ],
      OptionalReference = guid1,
      OptionalTimestamp = now,
      OptionalCount = 42,
      RelatedIds = [guid1, guid2],
      OptionalRelatedIds = [guid1, null],
      Status = TenantStatus.Active
    };

    var row = _createComplexRow(rowId, model);

    // Act
    _context!.Set<PerspectiveRow<ComplexModel>>().Add(row);
    await _context.SaveChangesAsync(cancellationToken);
    _context.ChangeTracker.Clear();

    var retrieved = await _context.Set<PerspectiveRow<ComplexModel>>()
        .FirstOrDefaultAsync(r => r.Id == rowId, cancellationToken);

    // Assert
    await Assert.That(retrieved).IsNotNull();
    await Assert.That(retrieved!.Data.Name).IsEqualTo("Complex Model");
    await Assert.That(retrieved.Data.StringMetadata.First(a => a.Key == "Key1").Value).IsEqualTo("Value1");
    await Assert.That(retrieved.Data.StringMetadata.First(a => a.Key == "Key2").Value).IsEqualTo("Value2");
    await Assert.That(retrieved.Data.OptionalReference).IsEqualTo(guid1);
    await Assert.That(retrieved.Data.OptionalCount).IsEqualTo(42);
    await Assert.That(retrieved.Data.RelatedIds.Count).IsEqualTo(2);
    await Assert.That(retrieved.Data.OptionalRelatedIds.Count).IsEqualTo(2);
    await Assert.That(retrieved.Data.Status).IsEqualTo(TenantStatus.Active);
  }

  // ==================== HELPER METHODS ====================

  private static PerspectiveRow<TenantModel> _createRow(Guid id, TenantModel model) {
    return new PerspectiveRow<TenantModel> {
      Id = id,
      Data = model,
      Metadata = new PerspectiveMetadata {
        EventType = "TenantCreated",
        EventId = Guid.NewGuid().ToString(),
        Timestamp = DateTime.UtcNow
      },
      Scope = new PerspectiveScope(),
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      Version = 1
    };
  }

  private static PerspectiveRow<ComplexModel> _createComplexRow(Guid id, ComplexModel model) {
    return new PerspectiveRow<ComplexModel> {
      Id = id,
      Data = model,
      Metadata = new PerspectiveMetadata {
        EventType = "ComplexModelCreated",
        EventId = Guid.NewGuid().ToString(),
        Timestamp = DateTime.UtcNow
      },
      Scope = new PerspectiveScope(),
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      Version = 1
    };
  }
}
