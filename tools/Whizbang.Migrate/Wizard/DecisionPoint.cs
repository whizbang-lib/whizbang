namespace Whizbang.Migrate.Wizard;

/// <summary>
/// Represents a single migration decision point with code preview.
/// </summary>
/// <docs>migrate-from-marten-wolverine/cli-wizard</docs>
public sealed class DecisionPoint {
  /// <summary>
  /// Path to the file containing this decision point.
  /// </summary>
  public string FilePath { get; init; } = string.Empty;

  /// <summary>
  /// Line number where the pattern is found.
  /// </summary>
  public int LineNumber { get; init; }

  /// <summary>
  /// Display name for the decision point (e.g., class name).
  /// </summary>
  public string DisplayName { get; init; } = string.Empty;

  /// <summary>
  /// Migration category this decision belongs to.
  /// </summary>
  public MigrationCategory Category { get; init; }

  /// <summary>
  /// Original source code snippet.
  /// </summary>
  public string OriginalCode { get; init; } = string.Empty;

  /// <summary>
  /// Available options for this decision.
  /// </summary>
  public List<DecisionOption> Options { get; init; } = [];

  /// <summary>
  /// The selected option key.
  /// </summary>
  public string? SelectedOption { get; private set; }

  /// <summary>
  /// Whether this decision has been made.
  /// </summary>
  public bool IsDecided => SelectedOption is not null;

  /// <summary>
  /// Whether to apply this decision to all similar items.
  /// </summary>
  public bool ApplyToAll { get; private set; }

  /// <summary>
  /// Formatted file location for display.
  /// </summary>
  public string FileLocation => $"{FilePath}:{LineNumber}";

  /// <summary>
  /// Creates a new decision point.
  /// </summary>
  public static DecisionPoint Create(
      string filePath,
      int lineNumber,
      string displayName,
      MigrationCategory category,
      string originalCode,
      List<DecisionOption> options) {
    return new DecisionPoint {
      FilePath = filePath,
      LineNumber = lineNumber,
      DisplayName = displayName,
      Category = category,
      OriginalCode = originalCode,
      Options = options
    };
  }

  /// <summary>
  /// Selects an option for this decision point.
  /// </summary>
  /// <param name="optionKey">The key of the option to select.</param>
  /// <param name="applyToAll">Whether to apply this decision to all similar items.</param>
  public void SelectOption(string optionKey, bool applyToAll = false) {
    SelectedOption = optionKey;
    ApplyToAll = applyToAll;
  }

  /// <summary>
  /// Gets the transformed code for the selected option.
  /// </summary>
  /// <returns>The transformed code, or null if skip was selected.</returns>
  public string? GetSelectedTransformedCode() {
    if (SelectedOption is null) {
      return null;
    }

    var option = Options.Find(o => o.Key == SelectedOption);
    return option?.TransformedCode;
  }

  /// <summary>
  /// Gets the recommended option, if any.
  /// </summary>
  public DecisionOption? GetRecommendedOption() {
    return Options.Find(o => o.IsRecommended);
  }
}

/// <summary>
/// Represents an option for a decision point.
/// </summary>
/// <param name="Key">Single character key (A, B, C, etc.).</param>
/// <param name="Label">Human-readable label for the option.</param>
/// <param name="TransformedCode">The transformed code for this option, or null for skip.</param>
/// <param name="IsRecommended">Whether this is the recommended option.</param>
public sealed record DecisionOption(
    string Key,
    string Label,
    string? TransformedCode,
    bool IsRecommended);
