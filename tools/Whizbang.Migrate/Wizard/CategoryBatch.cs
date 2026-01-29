namespace Whizbang.Migrate.Wizard;

/// <summary>
/// Represents a batch of migration items grouped by category.
/// </summary>
/// <docs>migrate-from-marten-wolverine/cli-wizard</docs>
public sealed class CategoryBatch {
  /// <summary>
  /// The migration category.
  /// </summary>
  public MigrationCategory Category { get; init; }

  /// <summary>
  /// Human-readable display name for the category.
  /// </summary>
  public string DisplayName { get; init; } = string.Empty;

  /// <summary>
  /// Items in this category.
  /// </summary>
  public List<MigrationItem> Items { get; init; } = [];

  /// <summary>
  /// Total number of items in this batch.
  /// </summary>
  public int TotalCount => Items.Count;

  /// <summary>
  /// Number of completed items.
  /// </summary>
  public int CompletedCount => Items.Count(i => i.IsComplete);

  /// <summary>
  /// Whether all items in this batch are complete.
  /// </summary>
  public bool IsComplete => TotalCount > 0 && CompletedCount == TotalCount;

  /// <summary>
  /// Progress percentage (0-100).
  /// </summary>
  public int ProgressPercentage => TotalCount == 0 ? 0 : (CompletedCount * 100) / TotalCount;

  /// <summary>
  /// Creates a new category batch.
  /// </summary>
  public static CategoryBatch Create(MigrationCategory category, IEnumerable<MigrationItem> items) {
    return new CategoryBatch {
      Category = category,
      DisplayName = GetDisplayName(category),
      Items = items.ToList()
    };
  }

  /// <summary>
  /// Marks an item as complete by index.
  /// </summary>
  public void MarkItemComplete(int index) {
    if (index >= 0 && index < Items.Count) {
      Items[index].IsComplete = true;
    }
  }

  /// <summary>
  /// Gets the index of the next incomplete item, or -1 if all complete.
  /// </summary>
  public int GetNextIncompleteIndex() {
    for (var i = 0; i < Items.Count; i++) {
      if (!Items[i].IsComplete) {
        return i;
      }
    }
    return -1;
  }

  /// <summary>
  /// Gets the display name for a category.
  /// </summary>
  public static string GetDisplayName(MigrationCategory category) {
    return category switch {
      MigrationCategory.Handlers => "Handlers",
      MigrationCategory.Projections => "Projections",
      MigrationCategory.EventStore => "Event Store Operations",
      MigrationCategory.IdGeneration => "ID Generation",
      MigrationCategory.DiRegistration => "DI Registration",
      _ => category.ToString()
    };
  }
}

/// <summary>
/// Categories of migration items.
/// </summary>
public enum MigrationCategory {
  /// <summary>
  /// Wolverine handlers to Whizbang receptors.
  /// </summary>
  Handlers,

  /// <summary>
  /// Marten projections to Whizbang perspectives.
  /// </summary>
  Projections,

  /// <summary>
  /// Marten event store operations.
  /// </summary>
  EventStore,

  /// <summary>
  /// ID generation (Guid.NewGuid, CombGuid).
  /// </summary>
  IdGeneration,

  /// <summary>
  /// DI registration changes.
  /// </summary>
  DiRegistration
}

/// <summary>
/// Represents a single item to be migrated.
/// </summary>
public sealed class MigrationItem {
  /// <summary>
  /// Path to the file containing this item.
  /// </summary>
  public string FilePath { get; init; }

  /// <summary>
  /// Display name for the item (e.g., class name).
  /// </summary>
  public string DisplayName { get; init; }

  /// <summary>
  /// Type of migration item.
  /// </summary>
  public MigrationItemType ItemType { get; init; }

  /// <summary>
  /// Line number where the item is defined.
  /// </summary>
  public int LineNumber { get; init; }

  /// <summary>
  /// Whether this item has been processed.
  /// </summary>
  public bool IsComplete { get; set; }

  /// <summary>
  /// The decision made for this item.
  /// </summary>
  public DecisionChoice? Decision { get; set; }

  /// <summary>
  /// Original source code snippet.
  /// </summary>
  public string? OriginalCode { get; set; }

  /// <summary>
  /// Transformed source code snippet.
  /// </summary>
  public string? TransformedCode { get; set; }

  /// <summary>
  /// Creates a new migration item.
  /// </summary>
  public MigrationItem(string filePath, string displayName, MigrationItemType itemType, int lineNumber = 0) {
    FilePath = filePath;
    DisplayName = displayName;
    ItemType = itemType;
    LineNumber = lineNumber;
  }
}

/// <summary>
/// Types of migration items.
/// </summary>
public enum MigrationItemType {
  /// <summary>
  /// A Wolverine handler class.
  /// </summary>
  Handler,

  /// <summary>
  /// A Marten projection class.
  /// </summary>
  Projection,

  /// <summary>
  /// An event store operation (StartStream, Append, etc.).
  /// </summary>
  EventStoreOperation,

  /// <summary>
  /// A Guid generation call.
  /// </summary>
  GuidGeneration,

  /// <summary>
  /// A DI registration call.
  /// </summary>
  DiRegistration
}
