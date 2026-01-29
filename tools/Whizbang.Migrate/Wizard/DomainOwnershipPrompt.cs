using Whizbang.Migrate.Analysis;

namespace Whizbang.Migrate.Wizard;

/// <summary>
/// Wizard prompt for domain ownership configuration.
/// Displays detected domains and allows user to select which domains this service owns.
/// </summary>
/// <docs>migrate-from-marten-wolverine/cli-wizard#domain-ownership</docs>
public sealed class DomainOwnershipPrompt {
  private const string BOX_TOP_LEFT = "\u250c";
  private const string BOX_TOP_RIGHT = "\u2510";
  private const string BOX_BOTTOM_LEFT = "\u2514";
  private const string BOX_BOTTOM_RIGHT = "\u2518";
  private const string BOX_HORIZONTAL = "\u2500";
  private const string BOX_VERTICAL = "\u2502";
  private const string BOX_T_LEFT = "\u251c";
  private const string BOX_T_RIGHT = "\u2524";
  private const int BOX_WIDTH = 65;

  private readonly DomainDetectionResult _detectionResult;
  private readonly HashSet<string> _selectedDomains;

  /// <summary>
  /// Creates a new domain ownership prompt with detected domains.
  /// </summary>
  /// <param name="detectionResult">Detection result from DomainOwnershipDetector.</param>
  public DomainOwnershipPrompt(DomainDetectionResult detectionResult) {
    _detectionResult = detectionResult;
    _selectedDomains = [];

    // Pre-select the most common domain
    if (detectionResult.MostCommon is not null) {
      _selectedDomains.Add(detectionResult.MostCommon.DomainName);
    }
  }

  /// <summary>
  /// Gets the currently selected domains.
  /// </summary>
  public IReadOnlySet<string> SelectedDomains => _selectedDomains;

  /// <summary>
  /// Renders the domain ownership prompt to the console.
  /// </summary>
  /// <param name="writer">Optional text writer (defaults to Console.Out).</param>
  public void Render(TextWriter? writer = null) {
    writer ??= Console.Out;

    _renderHeader("Domain Ownership Configuration", writer);

    if (!_detectionResult.HasDetections) {
      writer.WriteLine($"{BOX_VERTICAL}                                                                 {BOX_VERTICAL}");
      writer.WriteLine($"{BOX_VERTICAL}  No domain patterns detected in your codebase.                  {BOX_VERTICAL}");
      writer.WriteLine($"{BOX_VERTICAL}                                                                 {BOX_VERTICAL}");
      writer.WriteLine($"{BOX_VERTICAL}  You can manually specify domains using:                        {BOX_VERTICAL}");
      writer.WriteLine($"{BOX_VERTICAL}    options.Routing.OwnDomains(\"orders\", \"inventory\");            {BOX_VERTICAL}");
      writer.WriteLine($"{BOX_VERTICAL}                                                                 {BOX_VERTICAL}");
      _renderFooter(writer);
      return;
    }

    writer.WriteLine($"{BOX_VERTICAL}                                                                 {BOX_VERTICAL}");
    writer.WriteLine($"{BOX_VERTICAL}  We detected these domain patterns in your codebase:            {BOX_VERTICAL}");
    writer.WriteLine($"{BOX_VERTICAL}                                                                 {BOX_VERTICAL}");

    for (var i = 0; i < _detectionResult.DetectedDomains.Count; i++) {
      var domain = _detectionResult.DetectedDomains[i];
      var isSelected = _selectedDomains.Contains(domain.DomainName);
      var checkbox = isSelected ? "[x]" : "[ ]";
      var recommended = domain == _detectionResult.MostCommon ? " (Recommended - most common)" : "";
      var source = domain.FromNamespace ? "namespace" : "type names";
      var line = $"  {checkbox} {domain.DomainName} ({domain.OccurrenceCount} types, from {source}){recommended}";

      writer.WriteLine($"{BOX_VERTICAL}{line,-BOX_WIDTH}{BOX_VERTICAL}");
    }

    writer.WriteLine($"{BOX_VERTICAL}                                                                 {BOX_VERTICAL}");
    _renderDivider(writer);
    writer.WriteLine($"{BOX_VERTICAL}                                                                 {BOX_VERTICAL}");
    writer.WriteLine($"{BOX_VERTICAL}  Which domains does THIS service own?                           {BOX_VERTICAL}");
    writer.WriteLine($"{BOX_VERTICAL}  (Commands to owned domains route to this service)              {BOX_VERTICAL}");
    writer.WriteLine($"{BOX_VERTICAL}                                                                 {BOX_VERTICAL}");
    writer.WriteLine($"{BOX_VERTICAL}  Enter domain numbers to toggle (e.g., \"1,2\"), or:              {BOX_VERTICAL}");
    writer.WriteLine($"{BOX_VERTICAL}    [A] Accept current selection                                 {BOX_VERTICAL}");
    writer.WriteLine($"{BOX_VERTICAL}    [N] None - I'll configure manually                           {BOX_VERTICAL}");
    writer.WriteLine($"{BOX_VERTICAL}    [C] Custom - Enter domain names                              {BOX_VERTICAL}");
    writer.WriteLine($"{BOX_VERTICAL}                                                                 {BOX_VERTICAL}");
    _renderFooter(writer);

    writer.Write("  Selection: ");
  }

  /// <summary>
  /// Processes user input and updates selection.
  /// </summary>
  /// <param name="input">User input string.</param>
  /// <returns>True if input was valid and processed, false otherwise.</returns>
  public bool ProcessInput(string? input) {
    if (string.IsNullOrWhiteSpace(input)) {
      return false;
    }

    input = input.Trim().ToUpperInvariant();

    switch (input) {
      case "A":
        // Accept current selection - already set
        return true;

      case "N":
        // None selected
        _selectedDomains.Clear();
        return true;

      case "C":
        // Custom - will be handled by caller
        return false;

      default:
        // Try to parse as domain indices
        return _processToggleInput(input);
    }
  }

  /// <summary>
  /// Sets custom domain names directly.
  /// </summary>
  /// <param name="domains">Domain names to set.</param>
  public void SetCustomDomains(IEnumerable<string> domains) {
    _selectedDomains.Clear();
    foreach (var domain in domains) {
      if (!string.IsNullOrWhiteSpace(domain)) {
        _selectedDomains.Add(domain.Trim().ToLowerInvariant());
      }
    }
  }

  /// <summary>
  /// Toggles selection of a domain by index (1-based).
  /// </summary>
  /// <param name="index">1-based index of the domain.</param>
  /// <returns>True if toggle was successful.</returns>
  public bool ToggleDomain(int index) {
    if (index < 1 || index > _detectionResult.DetectedDomains.Count) {
      return false;
    }

    var domain = _detectionResult.DetectedDomains[index - 1].DomainName;

    // Use Remove's return value directly - returns true if removed, false if not present
    if (!_selectedDomains.Remove(domain)) {
      _selectedDomains.Add(domain);
    }

    return true;
  }

  /// <summary>
  /// Applies the selection to a RoutingDecisions instance.
  /// </summary>
  /// <param name="decisions">The routing decisions to update.</param>
  public void ApplyTo(RoutingDecisions decisions) {
    decisions.OwnedDomains = [.. _selectedDomains];
    decisions.DetectedDomains = _detectionResult.DetectedDomains
        .Select(d => d.DomainName)
        .ToList();
    decisions.Confirmed = true;
  }

  private bool _processToggleInput(string input) {
    // Try to parse comma-separated indices
    var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var anyToggled = false;

    foreach (var part in parts) {
      if (int.TryParse(part, out var index)) {
        if (ToggleDomain(index)) {
          anyToggled = true;
        }
      }
    }

    return anyToggled;
  }

  private static void _renderHeader(string title, TextWriter writer) {
    writer.WriteLine($"{BOX_TOP_LEFT}{new string(BOX_HORIZONTAL[0], BOX_WIDTH)}{BOX_TOP_RIGHT}");
    writer.WriteLine($"{BOX_VERTICAL}  {title,-(BOX_WIDTH - 2)}{BOX_VERTICAL}");
    writer.WriteLine($"{BOX_T_LEFT}{new string(BOX_HORIZONTAL[0], BOX_WIDTH)}{BOX_T_RIGHT}");
  }

  private static void _renderDivider(TextWriter writer) {
    writer.WriteLine($"{BOX_T_LEFT}{new string(BOX_HORIZONTAL[0], BOX_WIDTH)}{BOX_T_RIGHT}");
  }

  private static void _renderFooter(TextWriter writer) {
    writer.WriteLine($"{BOX_BOTTOM_LEFT}{new string(BOX_HORIZONTAL[0], BOX_WIDTH)}{BOX_BOTTOM_RIGHT}");
  }
}
