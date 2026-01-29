namespace Whizbang.Migrate.Wizard;

/// <summary>
/// Renders wizard UI components to the console.
/// </summary>
/// <docs>migrate-from-marten-wolverine/cli-wizard</docs>
public static class ConsoleRenderer {
  private const string BOX_TOP_LEFT = "┌";
  private const string BOX_TOP_RIGHT = "┐";
  private const string BOX_BOTTOM_LEFT = "└";
  private const string BOX_BOTTOM_RIGHT = "┘";
  private const string BOX_HORIZONTAL = "─";
  private const string BOX_VERTICAL = "│";
  private const string FILLED_BLOCK = "█";
  private const string EMPTY_BLOCK = "░";

  /// <summary>
  /// Renders the main wizard menu based on current migration state.
  /// </summary>
  public static void RenderMainMenu(DetectedMigrationState state, TextWriter? writer = null) {
    writer ??= Console.Out;

    _renderHeader("Whizbang Migration Wizard", writer);

    if (state.HasMigrationInProgress) {
      _renderInProgressMenu(state, writer);
    } else {
      _renderFreshStartMenu(state, writer);
    }
  }

  /// <summary>
  /// Renders the category selection menu.
  /// </summary>
  public static void RenderCategoryMenu(List<CategoryBatch> batches, TextWriter? writer = null) {
    writer ??= Console.Out;

    _renderHeader("Categories to Review", writer);

    for (var i = 0; i < batches.Count; i++) {
      var batch = batches[i];
      var status = batch.IsComplete ? "✓" : "○";
      writer.WriteLine($"  [{i + 1}] {batch.DisplayName} ({batch.TotalCount} items) {status}");
    }

    writer.WriteLine();
    writer.WriteLine("  Select category [1-" + batches.Count + "]: ");
  }

  /// <summary>
  /// Renders a single decision point with code preview.
  /// </summary>
  public static void RenderDecisionPoint(
      DecisionPoint point,
      int currentIndex,
      int totalCount,
      TextWriter? writer = null) {
    writer ??= Console.Out;

    var categoryName = CategoryBatch.GetDisplayName(point.Category);
    _renderHeader($"{categoryName} Migration [{currentIndex}/{totalCount}]", writer);

    writer.WriteLine($"  File: {point.FileLocation}");
    writer.WriteLine();

    // Original code
    writer.WriteLine("  BEFORE:");
    RenderCodeBlock(point.OriginalCode, writer);
    writer.WriteLine();

    // Options
    writer.WriteLine("  CONVERSION OPTIONS:");
    writer.WriteLine();

    foreach (var option in point.Options) {
      var recommended = option.IsRecommended ? " (Recommended)" : "";
      writer.WriteLine($"  [{option.Key}] {option.Label}{recommended}");

      if (option.TransformedCode is not null) {
        RenderCodeBlock(option.TransformedCode, writer, indent: 6);
      }

      writer.WriteLine();
    }

    writer.WriteLine("  Select option: ");
  }

  /// <summary>
  /// Renders a code block with border.
  /// </summary>
  public static void RenderCodeBlock(string code, TextWriter? writer = null, int indent = 4) {
    writer ??= Console.Out;

    var indentStr = new string(' ', indent);
    var lines = code.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    var maxWidth = Math.Max(lines.Max(l => l.Length), 40);

    writer.WriteLine($"{indentStr}{BOX_TOP_LEFT}{new string(BOX_HORIZONTAL[0], maxWidth + 2)}{BOX_TOP_RIGHT}");

    foreach (var line in lines) {
      writer.WriteLine($"{indentStr}{BOX_VERTICAL} {line.PadRight(maxWidth)} {BOX_VERTICAL}");
    }

    writer.WriteLine($"{indentStr}{BOX_BOTTOM_LEFT}{new string(BOX_HORIZONTAL[0], maxWidth + 2)}{BOX_BOTTOM_RIGHT}");
  }

  /// <summary>
  /// Renders a progress bar.
  /// </summary>
  public static void RenderProgressBar(int completed, int total, TextWriter? writer = null, int width = 20) {
    writer ??= Console.Out;

    var percentage = total == 0 ? 0 : (completed * 100) / total;
    var filledWidth = total == 0 ? 0 : (completed * width) / total;
    var emptyWidth = width - filledWidth;

    var filled = new string(FILLED_BLOCK[0], filledWidth);
    var empty = new string(EMPTY_BLOCK[0], emptyWidth);

    writer.WriteLine($"  [{filled}{empty}] {percentage}% ({completed}/{total})");
  }

  /// <summary>
  /// Renders a warning message.
  /// </summary>
  public static void RenderWarning(string message, TextWriter? writer = null) {
    writer ??= Console.Out;
    writer.WriteLine($"  Warning: {message}");
  }

  /// <summary>
  /// Renders a success message.
  /// </summary>
  public static void RenderSuccess(string message, TextWriter? writer = null) {
    writer ??= Console.Out;
    writer.WriteLine($"  {message}");
  }

  /// <summary>
  /// Renders an error message.
  /// </summary>
  public static void RenderError(string message, TextWriter? writer = null) {
    writer ??= Console.Out;
    writer.WriteLine($"  Error: {message}");
  }

  private static void _renderHeader(string title, TextWriter writer) {
    var width = 65;
    writer.WriteLine($"{BOX_TOP_LEFT}{new string(BOX_HORIZONTAL[0], width)}{BOX_TOP_RIGHT}");
    writer.WriteLine($"{BOX_VERTICAL}  {title.PadRight(width - 2)}{BOX_VERTICAL}");
    writer.WriteLine($"{BOX_TOP_LEFT}{new string(BOX_HORIZONTAL[0], width)}{BOX_TOP_RIGHT}");
    writer.WriteLine();
  }

  private static void _renderInProgressMenu(DetectedMigrationState state, TextWriter writer) {
    writer.WriteLine("  Migration in progress detected!");
    writer.WriteLine($"  Project: {state.ProjectPath}");

    if (state.StartedAt.HasValue) {
      writer.WriteLine($"  Started: {state.StartedAt:g}");
    }

    if (state.CompletedCategories.Count > 0) {
      writer.WriteLine($"  Completed: {string.Join(", ", state.CompletedCategories)}");
    }

    if (state.CurrentCategory is not null) {
      writer.WriteLine($"  Current: {state.CurrentCategory} (item {state.CurrentItem})");
    }

    writer.WriteLine();
    writer.WriteLine("  What would you like to do?");
    writer.WriteLine();
    writer.WriteLine("  [1] Continue migration");
    writer.WriteLine("  [2] Review/edit decisions");
    writer.WriteLine("  [3] Revert all changes");
    writer.WriteLine("  [4] Start fresh");
    writer.WriteLine("  [5] View status");
    writer.WriteLine();
    writer.WriteLine("  Select option [1-5]: ");
  }

  private static void _renderFreshStartMenu(DetectedMigrationState state, TextWriter writer) {
    writer.WriteLine("  Migrate from Marten/Wolverine to Whizbang");
    writer.WriteLine();

    if (state.Status == MigrationStatus.Completed) {
      writer.WriteLine("  Previous migration completed successfully.");
      writer.WriteLine();
    }

    writer.WriteLine("  [1] Analyze codebase");
    writer.WriteLine("  [2] Start new migration");
    writer.WriteLine("  [3] Load existing decisions");
    writer.WriteLine("  [4] Help");
    writer.WriteLine();
    writer.WriteLine("  Select option [1-4]: ");
  }
}
