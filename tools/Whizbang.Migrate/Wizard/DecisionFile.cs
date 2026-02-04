using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whizbang.Migrate.Wizard;

/// <summary>
/// JSON serialization context for AOT-compatible serialization.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(DecisionFile))]
[JsonSerializable(typeof(SchemaDecisions))]
[JsonSerializable(typeof(TenantDecisions))]
[JsonSerializable(typeof(StreamIdDecisions))]
[JsonSerializable(typeof(RoutingDecisions))]
[JsonSerializable(typeof(CustomBaseClassDecisions))]
[JsonSerializable(typeof(UnknownInterfaceDecisions))]
internal sealed partial class DecisionFileJsonContext : JsonSerializerContext { }

/// <summary>
/// Represents a migration decision file that stores all migration choices and state.
/// Decision files can be stored outside the working tree to survive git operations.
/// </summary>
/// <docs>migrate-from-marten-wolverine/cli-wizard</docs>
public sealed class DecisionFile {

  /// <summary>
  /// Version of the decision file format.
  /// </summary>
  public string Version { get; set; } = "1.0";

  /// <summary>
  /// Path to the project being migrated.
  /// </summary>
  public string ProjectPath { get; set; } = string.Empty;

  /// <summary>
  /// When this decision file was created.
  /// </summary>
  public DateTimeOffset GeneratedAt { get; set; }

  /// <summary>
  /// Current migration state tracking.
  /// </summary>
  public MigrationState State { get; set; } = new();

  /// <summary>
  /// All migration decisions by category.
  /// </summary>
  public MigrationDecisions Decisions { get; set; } = new();

  /// <summary>
  /// Creates a new decision file for a project.
  /// </summary>
  public static DecisionFile Create(string projectPath) {
    return new DecisionFile {
      ProjectPath = projectPath,
      GeneratedAt = DateTimeOffset.UtcNow,
      State = new MigrationState {
        Status = MigrationStatus.NotStarted
      },
      Decisions = new MigrationDecisions()
    };
  }

  /// <summary>
  /// Serializes the decision file to JSON.
  /// </summary>
  public string ToJson() {
    return JsonSerializer.Serialize(this, DecisionFileJsonContext.Default.DecisionFile);
  }

  /// <summary>
  /// Deserializes a decision file from JSON.
  /// </summary>
  public static DecisionFile FromJson(string json) {
    return JsonSerializer.Deserialize(json, DecisionFileJsonContext.Default.DecisionFile)
        ?? throw new InvalidOperationException("Failed to deserialize decision file");
  }

  /// <summary>
  /// Saves the decision file to a path.
  /// </summary>
  /// <param name="path">Path to save the file.</param>
  /// <param name="includeComments">If true, generates JSONC with helpful comments.</param>
  /// <param name="ct">Cancellation token.</param>
  public async Task SaveAsync(string path, bool includeComments = false, CancellationToken ct = default) {
    var directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) {
      Directory.CreateDirectory(directory);
    }

    var content = includeComments ? ToJsonWithComments() : ToJson();
    await File.WriteAllTextAsync(path, content, ct);
  }

  /// <summary>
  /// Generates JSONC content with helpful comments explaining each setting.
  /// </summary>
  public string ToJsonWithComments() {
    var timestamp = GeneratedAt.ToString("O");

    return $$"""
{
  // Whizbang Migration Decision File
  // Edit this file to control migration behavior, then run:
  //   whizbang-migrate apply -p {{ProjectPath}} -d <this-file>

  "version": "{{Version}}",
  "project_path": "{{ProjectPath}}",
  "generated_at": "{{timestamp}}",

  // Current migration state (managed by the tool)
  "state": {
    "status": "{{State.Status}}",
    "started_at": null,
    "last_updated_at": null,
    "completed_at": null,
    "git_commit_before": null,
    "completed_categories": [],
    "current_category": null,
    "current_item": 0
  },

  "decisions": {
    // ═══════════════════════════════════════════════════════════════════════════
    // HANDLER MIGRATION: IHandle<T> → IReceptor<T, TResult>
    // ═══════════════════════════════════════════════════════════════════════════
    // Transforms Wolverine message handlers to Whizbang receptors.
    // - Handle/HandleAsync methods become ReceiveAsync
    // - [WolverineHandler] attributes are removed
    // - MessageContext → MessageEnvelope
    // - IMessageBus → IDispatcher
    "handlers": {
      // Options: "Convert", "Skip", "ConvertWithWarning", "Prompt"
      "default": "{{Decisions.Handlers.Default}}",

      // Per-file overrides (use full or relative paths)
      // Example: "src/Handlers/MyHandler.cs": "Skip"
      "overrides": {}
    },

    // ═══════════════════════════════════════════════════════════════════════════
    // PROJECTION MIGRATION: SingleStreamProjection → IPerspectiveFor
    // ═══════════════════════════════════════════════════════════════════════════
    // Transforms Marten projections to Whizbang perspectives.
    "projections": {
      // Interface to use for single-stream projections (aggregates)
      "single_stream": "{{Decisions.Projections.SingleStream}}",

      // Interface to use for multi-stream projections (cross-aggregate views)
      "multi_stream": "{{Decisions.Projections.MultiStream}}",

      "default": "{{Decisions.Projections.Default}}",
      "overrides": {}
    },

    // ═══════════════════════════════════════════════════════════════════════════
    // EVENT STORE OPERATIONS
    // ═══════════════════════════════════════════════════════════════════════════
    // Controls transformation of Marten IDocumentSession/IDocumentStore patterns.
    "event_store": {
      // AppendExclusive: Marten's exclusive append (optimistic concurrency)
      // ConvertWithWarning adds a TODO comment for manual review
      "append_exclusive": "{{Decisions.EventStore.AppendExclusive}}",

      // StartStream: Creates new event stream
      // Convert transforms to AppendAsync with new stream ID
      "start_stream": "{{Decisions.EventStore.StartStream}}",

      // SaveChangesAsync: Marten's unit-of-work commit
      // Skip removes these calls (Whizbang auto-commits)
      "save_changes": "{{Decisions.EventStore.SaveChanges}}"
    },

    // ═══════════════════════════════════════════════════════════════════════════
    // ID GENERATION: Guid.NewGuid() → IWhizbangIdProvider
    // ═══════════════════════════════════════════════════════════════════════════
    // Whizbang uses IWhizbangIdProvider for testable, deterministic IDs.
    "id_generation": {
      // Guid.NewGuid() calls - Convert injects IWhizbangIdProvider
      // Use "Skip" if you want to keep Guid.NewGuid() as-is
      "guid_new_guid": "{{Decisions.IdGeneration.GuidNewGuid}}",

      // CombGuidIdGeneration.NewGuid() (Marten's sequential GUIDs)
      "comb_guid": "{{Decisions.IdGeneration.CombGuid}}"
    },

    // ═══════════════════════════════════════════════════════════════════════════
    // DI REGISTRATION
    // ═══════════════════════════════════════════════════════════════════════════
    // Transforms service registration patterns.
    "di_registration": {
      "default": "{{Decisions.DiRegistration.Default}}",
      "overrides": {}
    },

    // ═══════════════════════════════════════════════════════════════════════════
    // SCHEMA CONFIGURATION
    // ═══════════════════════════════════════════════════════════════════════════
    // How Whizbang tables are organized in the database.
    // This enables side-by-side migration with existing Marten tables.
    "schema": {
      // Strategy options:
      // - "SameDbDifferentSchema": mydb.whizbang.wb_* (recommended)
      // - "DifferentDbDefaultSchema": whizbang_db.public.wb_*
      // - "SameDbSameSchemaWithPrefix": mydb.public.wb_*
      // - "DifferentDbDifferentSchema": whizbang_db.events.wb_*
      "strategy": "{{Decisions.Schema.Strategy}}",

      // Schema name (when using SameDbDifferentSchema)
      "schema_name": "{{Decisions.Schema.SchemaName}}",

      // Table prefixes
      "infrastructure_prefix": "{{Decisions.Schema.InfrastructurePrefix}}",
      "perspective_prefix": "{{Decisions.Schema.PerspectivePrefix}}",

      // Connection string name (when using different database)
      "connection_string_name": null
    },

    // ═══════════════════════════════════════════════════════════════════════════
    // TENANT CONTEXT
    // ═══════════════════════════════════════════════════════════════════════════
    // Multi-tenancy configuration (detected automatically if present).
    "tenant": {
      "was_detected": false,
      "detected_property": null,
      "uses_marten_tenant_features": false,

      // Strategy options:
      // - "ScopeField": Store tenant in event scope (recommended)
      // - "ScopedDi": Per-tenant IEventStore registration
      // - "Manual": You handle it
      "strategy": "{{Decisions.Tenant.Strategy}}",
      "confirmed": false
    },

    // ═══════════════════════════════════════════════════════════════════════════
    // STREAM ID CONFIGURATION
    // ═══════════════════════════════════════════════════════════════════════════
    // How aggregate/stream IDs are detected and used.
    "stream_id": {
      "was_detected": false,
      "detected_property": null,
      "is_strongly_typed": false,
      "confirmed": false
    },

    // ═══════════════════════════════════════════════════════════════════════════
    // ROUTING CONFIGURATION
    // ═══════════════════════════════════════════════════════════════════════════
    // Message routing for inbox/outbox patterns.
    "routing": {
      // Domains owned by this service (commands routed to inbox)
      "owned_domains": [],
      "detected_domains": [],

      // Inbox strategy: "SharedTopic" or "DomainTopics"
      "inbox_strategy": "{{Decisions.Routing.InboxStrategy}}",
      "inbox_topic": null,
      "inbox_suffix": null,

      // Outbox strategy: "DomainTopics" or "SharedTopic"
      "outbox_strategy": "{{Decisions.Routing.OutboxStrategy}}",
      "outbox_topic": null,
      "confirmed": false
    },

    // ═══════════════════════════════════════════════════════════════════════════
    // CUSTOM BASE CLASSES
    // ═══════════════════════════════════════════════════════════════════════════
    // How to handle handlers that inherit from custom base classes.
    "custom_base_classes": {
      // Options: "Prompt", "RemoveInheritance", "KeepInheritance", "Skip"
      "default_strategy": "{{Decisions.CustomBaseClasses.DefaultStrategy}}",

      // Per-class strategies
      // Example: "BaseMessageHandler": "RemoveInheritance"
      "base_class_strategies": {},
      "confirmed": false
    },

    // ═══════════════════════════════════════════════════════════════════════════
    // UNKNOWN INTERFACES
    // ═══════════════════════════════════════════════════════════════════════════
    // How to handle handler parameters with unknown interface types.
    "unknown_interfaces": {
      // Options: "Prompt", "RemoveParameter", "KeepAndInject", "MapToWhizbang", "Skip"
      "default_strategy": "{{Decisions.UnknownInterfaces.DefaultStrategy}}",

      // Per-interface strategies
      // Example: "ICustomService": "KeepAndInject"
      "interface_strategies": {},
      "confirmed": false
    }
  }
}
""";
  }

  /// <summary>
  /// Loads a decision file from a path.
  /// </summary>
  public static async Task<DecisionFile> LoadAsync(string path, CancellationToken ct = default) {
    var json = await File.ReadAllTextAsync(path, ct);
    return FromJson(json);
  }

  /// <summary>
  /// Gets the default storage path for a project's decision file.
  /// Default: ~/.whizbang/migrations/{projectName}/decisions.json
  /// </summary>
  public static string GetDefaultPath(string projectName) {
    var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return Path.Combine(userHome, ".whizbang", "migrations", projectName, "decisions.json");
  }

  /// <summary>
  /// Updates the migration state with automatic timestamp update.
  /// </summary>
  public void UpdateState(Action<MigrationState> update) {
    update(State);
    State.LastUpdatedAt = DateTimeOffset.UtcNow;
  }

  /// <summary>
  /// Marks a category as complete and moves to the next.
  /// </summary>
  public void MarkCategoryComplete(string completedCategory, string? nextCategory) {
    UpdateState(state => {
      if (!state.CompletedCategories.Contains(completedCategory)) {
        state.CompletedCategories.Add(completedCategory);
      }
      state.CurrentCategory = nextCategory;
      state.CurrentItem = 0;
    });
  }

  /// <summary>
  /// Marks the entire migration as complete.
  /// </summary>
  public void MarkComplete() {
    UpdateState(state => {
      state.Status = MigrationStatus.Completed;
      state.CompletedAt = DateTimeOffset.UtcNow;
    });
  }

  /// <summary>
  /// Sets a handler decision for a specific file.
  /// </summary>
  public void SetHandlerDecision(string filePath, DecisionChoice choice) {
    Decisions.Handlers.Overrides[filePath] = choice;
  }

  /// <summary>
  /// Gets the handler decision for a file (override or default).
  /// </summary>
  public DecisionChoice GetHandlerDecision(string filePath) {
    return Decisions.Handlers.Overrides.TryGetValue(filePath, out var choice)
        ? choice
        : Decisions.Handlers.Default;
  }

  /// <summary>
  /// Sets a projection decision for a specific file.
  /// </summary>
  public void SetProjectionDecision(string filePath, DecisionChoice choice) {
    Decisions.Projections.Overrides[filePath] = choice;
  }

  /// <summary>
  /// Gets the projection decision for a file (override or default).
  /// </summary>
  public DecisionChoice GetProjectionDecision(string filePath) {
    return Decisions.Projections.Overrides.TryGetValue(filePath, out var choice)
        ? choice
        : Decisions.Projections.Default;
  }
}

/// <summary>
/// Tracks the current state of an in-progress migration.
/// </summary>
public sealed class MigrationState {
  /// <summary>
  /// Current status of the migration.
  /// </summary>
  public MigrationStatus Status { get; set; } = MigrationStatus.NotStarted;

  /// <summary>
  /// When the migration was started.
  /// </summary>
  public DateTimeOffset? StartedAt { get; set; }

  /// <summary>
  /// When the migration state was last updated.
  /// </summary>
  public DateTimeOffset? LastUpdatedAt { get; set; }

  /// <summary>
  /// When the migration was completed.
  /// </summary>
  public DateTimeOffset? CompletedAt { get; set; }

  /// <summary>
  /// Git commit hash before migration started (for revert).
  /// </summary>
  public string? GitCommitBefore { get; set; }

  /// <summary>
  /// Categories that have been fully processed.
  /// </summary>
  public List<string> CompletedCategories { get; set; } = [];

  /// <summary>
  /// Category currently being processed.
  /// </summary>
  public string? CurrentCategory { get; set; }

  /// <summary>
  /// Index of the current item being processed in the current category.
  /// </summary>
  public int CurrentItem { get; set; }
}

/// <summary>
/// Status of a migration operation.
/// </summary>
public enum MigrationStatus {
  /// <summary>
  /// Migration has not been started.
  /// </summary>
  NotStarted,

  /// <summary>
  /// Migration is in progress.
  /// </summary>
  InProgress,

  /// <summary>
  /// Migration has been completed.
  /// </summary>
  Completed,

  /// <summary>
  /// Migration was reverted.
  /// </summary>
  Reverted
}

/// <summary>
/// All migration decisions organized by category.
/// </summary>
public sealed class MigrationDecisions {
  /// <summary>
  /// Handler migration decisions.
  /// </summary>
  public CategoryDecisions Handlers { get; set; } = new();

  /// <summary>
  /// Projection migration decisions.
  /// </summary>
  public ProjectionDecisions Projections { get; set; } = new();

  /// <summary>
  /// Event store operation decisions.
  /// </summary>
  public EventStoreDecisions EventStore { get; set; } = new();

  /// <summary>
  /// ID generation decisions.
  /// </summary>
  public IdGenerationDecisions IdGeneration { get; set; } = new();

  /// <summary>
  /// DI registration decisions.
  /// </summary>
  public CategoryDecisions DiRegistration { get; set; } = new();

  /// <summary>
  /// Database schema configuration decisions.
  /// </summary>
  public SchemaDecisions Schema { get; set; } = new();

  /// <summary>
  /// Tenant context decisions.
  /// </summary>
  public TenantDecisions Tenant { get; set; } = new();

  /// <summary>
  /// Stream ID property decisions.
  /// </summary>
  public StreamIdDecisions StreamId { get; set; } = new();

  /// <summary>
  /// Routing configuration decisions.
  /// </summary>
  public RoutingDecisions Routing { get; set; } = new();

  /// <summary>
  /// Custom base class handling decisions.
  /// </summary>
  public CustomBaseClassDecisions CustomBaseClasses { get; set; } = new();

  /// <summary>
  /// Unknown interface parameter handling decisions.
  /// </summary>
  public UnknownInterfaceDecisions UnknownInterfaces { get; set; } = new();
}

/// <summary>
/// Generic category decisions with default and per-file overrides.
/// </summary>
public class CategoryDecisions {
  /// <summary>
  /// Default decision for this category.
  /// </summary>
  public DecisionChoice Default { get; set; } = DecisionChoice.Convert;

  /// <summary>
  /// Per-file overrides.
  /// </summary>
  public Dictionary<string, DecisionChoice> Overrides { get; set; } = [];
}

/// <summary>
/// Projection-specific decisions.
/// </summary>
public sealed class ProjectionDecisions : CategoryDecisions {
  /// <summary>
  /// Interface to use for single-stream projections.
  /// </summary>
  public string SingleStream { get; set; } = "IPerspectiveFor";

  /// <summary>
  /// Interface to use for multi-stream projections.
  /// </summary>
  public string MultiStream { get; set; } = "IGlobalPerspectiveFor";
}

/// <summary>
/// Event store operation decisions.
/// </summary>
public sealed class EventStoreDecisions {
  /// <summary>
  /// Decision for AppendExclusive operations.
  /// </summary>
  public DecisionChoice AppendExclusive { get; set; } = DecisionChoice.ConvertWithWarning;

  /// <summary>
  /// Decision for StartStream operations.
  /// </summary>
  public DecisionChoice StartStream { get; set; } = DecisionChoice.Convert;

  /// <summary>
  /// Decision for SaveChangesAsync operations.
  /// </summary>
  public DecisionChoice SaveChanges { get; set; } = DecisionChoice.Skip;
}

/// <summary>
/// ID generation decisions.
/// </summary>
public sealed class IdGenerationDecisions {
  /// <summary>
  /// Decision for Guid.NewGuid() calls.
  /// </summary>
  public DecisionChoice GuidNewGuid { get; set; } = DecisionChoice.Prompt;

  /// <summary>
  /// Decision for CombGuidIdGeneration.NewGuid() calls.
  /// </summary>
  public DecisionChoice CombGuid { get; set; } = DecisionChoice.Convert;
}

/// <summary>
/// A migration decision choice.
/// </summary>
public enum DecisionChoice {
  /// <summary>
  /// Convert to Whizbang equivalent.
  /// </summary>
  Convert,

  /// <summary>
  /// Skip this item (leave unchanged).
  /// </summary>
  Skip,

  /// <summary>
  /// Convert but add a warning/TODO comment.
  /// </summary>
  ConvertWithWarning,

  /// <summary>
  /// Prompt the user for a decision.
  /// </summary>
  Prompt,

  /// <summary>
  /// Apply this decision to all similar items.
  /// </summary>
  ApplyToAll
}

/// <summary>
/// Schema configuration decisions.
/// </summary>
public sealed class SchemaDecisions {
  /// <summary>
  /// The schema organization strategy.
  /// </summary>
  public SchemaStrategy Strategy { get; set; } = SchemaStrategy.SameDbDifferentSchema;

  /// <summary>
  /// Schema name to use (if applicable).
  /// </summary>
  public string SchemaName { get; set; } = "whizbang";

  /// <summary>
  /// Prefix for infrastructure tables.
  /// </summary>
  public string InfrastructurePrefix { get; set; } = "wb_";

  /// <summary>
  /// Prefix for perspective tables.
  /// </summary>
  public string PerspectivePrefix { get; set; } = "wb_per_";

  /// <summary>
  /// Connection string name to use (if different database).
  /// </summary>
  public string? ConnectionStringName { get; set; }
}

/// <summary>
/// Schema organization strategy.
/// </summary>
public enum SchemaStrategy {
  /// <summary>
  /// Same database, different schema (e.g., mydb.whizbang.wb_event_store).
  /// </summary>
  SameDbDifferentSchema,

  /// <summary>
  /// Different database, default schema (e.g., whizbang_db.public.wb_event_store).
  /// </summary>
  DifferentDbDefaultSchema,

  /// <summary>
  /// Same database, same schema with prefix (e.g., mydb.public.wb_event_store).
  /// </summary>
  SameDbSameSchemaWithPrefix,

  /// <summary>
  /// Different database, different schema (e.g., whizbang_db.events.wb_event_store).
  /// </summary>
  DifferentDbDifferentSchema
}

/// <summary>
/// Tenant context decisions.
/// </summary>
public sealed class TenantDecisions {
  /// <summary>
  /// Whether tenant context was detected in the codebase.
  /// </summary>
  public bool WasDetected { get; set; }

  /// <summary>
  /// The detected tenant property name (e.g., "TenantId", "OrganizationId").
  /// </summary>
  public string? DetectedProperty { get; set; }

  /// <summary>
  /// Whether Marten tenant features were detected.
  /// </summary>
  public bool UsesMartenTenantFeatures { get; set; }

  /// <summary>
  /// How tenant context should be handled.
  /// </summary>
  public TenantStrategy Strategy { get; set; } = TenantStrategy.ScopeField;

  /// <summary>
  /// User confirmed the tenant property choice.
  /// </summary>
  public bool Confirmed { get; set; }
}

/// <summary>
/// Tenant handling strategy.
/// </summary>
public enum TenantStrategy {
  /// <summary>
  /// Store tenant context in event scope field (recommended).
  /// </summary>
  ScopeField,

  /// <summary>
  /// Use scoped DI with per-tenant IEventStore registration.
  /// </summary>
  ScopedDi,

  /// <summary>
  /// Manual configuration by the user.
  /// </summary>
  Manual
}

/// <summary>
/// Stream ID property decisions.
/// </summary>
public sealed class StreamIdDecisions {
  /// <summary>
  /// Whether stream ID property was detected in the codebase.
  /// </summary>
  public bool WasDetected { get; set; }

  /// <summary>
  /// The detected stream ID property name (e.g., "OrderId", "StreamId").
  /// </summary>
  public string? DetectedProperty { get; set; }

  /// <summary>
  /// Whether the detected property is a strongly-typed ID.
  /// </summary>
  public bool IsStronglyTyped { get; set; }

  /// <summary>
  /// User confirmed the stream ID property choice.
  /// </summary>
  public bool Confirmed { get; set; }
}

/// <summary>
/// Routing configuration decisions.
/// </summary>
public sealed class RoutingDecisions {
  /// <summary>
  /// Domains owned by this service.
  /// Commands to owned domains are routed to this service's inbox.
  /// </summary>
  public List<string> OwnedDomains { get; set; } = [];

  /// <summary>
  /// Domains detected in the codebase (for user selection).
  /// </summary>
  public List<string> DetectedDomains { get; set; } = [];

  /// <summary>
  /// The inbox routing strategy to use.
  /// </summary>
  public InboxStrategyChoice InboxStrategy { get; set; } = InboxStrategyChoice.SharedTopic;

  /// <summary>
  /// Custom inbox topic name (when using SharedTopic strategy).
  /// </summary>
  public string? InboxTopic { get; set; }

  /// <summary>
  /// Custom inbox suffix (when using DomainTopics strategy).
  /// </summary>
  public string? InboxSuffix { get; set; }

  /// <summary>
  /// The outbox routing strategy to use.
  /// </summary>
  public OutboxStrategyChoice OutboxStrategy { get; set; } = OutboxStrategyChoice.DomainTopics;

  /// <summary>
  /// Custom outbox topic name (when using SharedTopic strategy).
  /// </summary>
  public string? OutboxTopic { get; set; }

  /// <summary>
  /// User confirmed the routing configuration.
  /// </summary>
  public bool Confirmed { get; set; }
}

/// <summary>
/// Inbox routing strategy choice.
/// </summary>
public enum InboxStrategyChoice {
  /// <summary>
  /// All commands route to a single shared topic with broker-side filtering.
  /// Default strategy - minimizes topic count.
  /// </summary>
  SharedTopic,

  /// <summary>
  /// Each domain has its own inbox topic.
  /// JDNext-style - explicit inbox per domain.
  /// </summary>
  DomainTopics
}

/// <summary>
/// Outbox routing strategy choice.
/// </summary>
public enum OutboxStrategyChoice {
  /// <summary>
  /// Each domain publishes to its own topic.
  /// Default strategy - clear domain separation.
  /// </summary>
  DomainTopics,

  /// <summary>
  /// All events publish to a single shared topic with metadata.
  /// </summary>
  SharedTopic
}

/// <summary>
/// Decisions about handling custom base classes found in handlers.
/// </summary>
public sealed class CustomBaseClassDecisions {
  /// <summary>
  /// Default strategy for handling custom base classes.
  /// </summary>
  public CustomBaseClassStrategy DefaultStrategy { get; set; } = CustomBaseClassStrategy.Prompt;

  /// <summary>
  /// Custom base classes detected in the codebase.
  /// Maps base class name to the strategy to use.
  /// </summary>
  public Dictionary<string, CustomBaseClassStrategy> BaseClassStrategies { get; set; } = [];

  /// <summary>
  /// Whether the user has confirmed the base class decisions.
  /// </summary>
  public bool Confirmed { get; set; }
}

/// <summary>
/// Strategy for handling custom handler base classes during migration.
/// </summary>
public enum CustomBaseClassStrategy {
  /// <summary>
  /// Prompt the user for each handler with this base class.
  /// </summary>
  Prompt,

  /// <summary>
  /// Remove the base class inheritance and implement Whizbang interfaces directly.
  /// The migrated handler will implement IReceptor directly without any base class.
  /// </summary>
  RemoveInheritance,

  /// <summary>
  /// Keep the base class inheritance and add Whizbang interface implementation.
  /// The user is expected to manually adapt the base class to work with Whizbang.
  /// Generates a TODO comment reminding the user to update the base class.
  /// </summary>
  KeepInheritance,

  /// <summary>
  /// Skip migration of handlers using this base class.
  /// The handler will be left unchanged with a warning.
  /// </summary>
  Skip
}

/// <summary>
/// Decisions about handling unknown interface parameters found in handlers.
/// </summary>
public sealed class UnknownInterfaceDecisions {
  /// <summary>
  /// Default strategy for handling unknown interface parameters.
  /// </summary>
  public UnknownInterfaceStrategy DefaultStrategy { get; set; } = UnknownInterfaceStrategy.Prompt;

  /// <summary>
  /// Unknown interfaces detected in the codebase.
  /// Maps interface name to the strategy to use.
  /// </summary>
  public Dictionary<string, UnknownInterfaceStrategy> InterfaceStrategies { get; set; } = [];

  /// <summary>
  /// Whether the user has confirmed the interface decisions.
  /// </summary>
  public bool Confirmed { get; set; }
}

/// <summary>
/// Strategy for handling unknown interface parameters during migration.
/// </summary>
public enum UnknownInterfaceStrategy {
  /// <summary>
  /// Prompt the user for each handler with this interface parameter.
  /// </summary>
  Prompt,

  /// <summary>
  /// Remove the parameter from the migrated handler.
  /// The functionality using this interface must be reimplemented.
  /// </summary>
  RemoveParameter,

  /// <summary>
  /// Keep the parameter and inject it via DI.
  /// The user is expected to ensure the interface is registered.
  /// </summary>
  KeepAndInject,

  /// <summary>
  /// Map this interface to a Whizbang equivalent.
  /// Used when the interface wraps known Marten/Wolverine types.
  /// </summary>
  MapToWhizbang,

  /// <summary>
  /// Skip migration of handlers using this interface.
  /// The handler will be left unchanged with a warning.
  /// </summary>
  Skip
}
