namespace Whizbang.Migrate.Wizard;

/// <summary>
/// Wizard prompt for inbox routing strategy selection.
/// Allows user to choose between shared topic and domain topic strategies.
/// </summary>
/// <docs>migrate-from-marten-wolverine/cli-wizard#inbox-strategy</docs>
public sealed class InboxStrategyPrompt {
  private const string BOX_TOP_LEFT = "\u250c";
  private const string BOX_TOP_RIGHT = "\u2510";
  private const string BOX_BOTTOM_LEFT = "\u2514";
  private const string BOX_BOTTOM_RIGHT = "\u2518";
  private const string BOX_HORIZONTAL = "\u2500";
  private const string BOX_VERTICAL = "\u2502";
  private const string BOX_T_LEFT = "\u251c";
  private const string BOX_T_RIGHT = "\u2524";
  private const int BOX_WIDTH = 65;

  private readonly IReadOnlyList<string> _ownedDomains;

  /// <summary>
  /// Gets the selected inbox strategy.
  /// </summary>
  public InboxStrategyChoice SelectedStrategy { get; private set; } = InboxStrategyChoice.SharedTopic;

  /// <summary>
  /// Gets the custom topic name (when using SharedTopic strategy).
  /// </summary>
  public string? CustomTopic { get; private set; }

  /// <summary>
  /// Gets the custom suffix (when using DomainTopics strategy).
  /// </summary>
  public string? CustomSuffix { get; private set; }

  /// <summary>
  /// Creates a new inbox strategy prompt.
  /// </summary>
  /// <param name="ownedDomains">The domains owned by this service (for example display).</param>
  public InboxStrategyPrompt(IReadOnlyList<string> ownedDomains) {
    _ownedDomains = ownedDomains;
  }

  /// <summary>
  /// Renders the inbox strategy prompt to the console.
  /// </summary>
  /// <param name="writer">Optional text writer (defaults to Console.Out).</param>
  public void Render(TextWriter? writer = null) {
    writer ??= Console.Out;

    _renderHeader("Inbox Routing Strategy", writer);

    writer.WriteLine($"{BOX_VERTICAL}                                                                 {BOX_VERTICAL}");
    writer.WriteLine($"{BOX_VERTICAL}  How should commands be routed to this service?                 {BOX_VERTICAL}");
    writer.WriteLine($"{BOX_VERTICAL}                                                                 {BOX_VERTICAL}");
    _renderDivider(writer);

    // Option A: Shared Topic (Recommended)
    writer.WriteLine($"{BOX_VERTICAL}                                                                 {BOX_VERTICAL}");
    writer.WriteLine($"{BOX_VERTICAL}  [A] Shared Topic (Recommended)                                 {BOX_VERTICAL}");
    writer.WriteLine($"{BOX_VERTICAL}                                                                 {BOX_VERTICAL}");
    writer.WriteLine($"{BOX_VERTICAL}      All commands route to \"whizbang.inbox\" with broker-side    {BOX_VERTICAL}");
    writer.WriteLine($"{BOX_VERTICAL}      filtering. Fewer topics, relies on ASB/RabbitMQ filtering. {BOX_VERTICAL}");
    writer.WriteLine($"{BOX_VERTICAL}                                                                 {BOX_VERTICAL}");

    if (_ownedDomains.Count > 0) {
      var exampleDomain = _ownedDomains[0];
      writer.WriteLine($"{BOX_VERTICAL}      Example: CreateOrder -> \"whizbang.inbox\" (filter: {exampleDomain.PadRight(10)}){BOX_VERTICAL}");
    } else {
      writer.WriteLine($"{BOX_VERTICAL}      Example: CreateOrder -> \"whizbang.inbox\" (with filter)    {BOX_VERTICAL}");
    }

    writer.WriteLine($"{BOX_VERTICAL}                                                                 {BOX_VERTICAL}");
    _renderDivider(writer);

    // Option B: Domain Topics
    writer.WriteLine($"{BOX_VERTICAL}                                                                 {BOX_VERTICAL}");
    writer.WriteLine($"{BOX_VERTICAL}  [B] Domain Topics                                              {BOX_VERTICAL}");
    writer.WriteLine($"{BOX_VERTICAL}                                                                 {BOX_VERTICAL}");
    writer.WriteLine($"{BOX_VERTICAL}      Each domain has its own inbox topic.                       {BOX_VERTICAL}");
    writer.WriteLine($"{BOX_VERTICAL}      More topics, simpler routing logic.                        {BOX_VERTICAL}");
    writer.WriteLine($"{BOX_VERTICAL}                                                                 {BOX_VERTICAL}");

    if (_ownedDomains.Count > 0) {
      var exampleDomain = _ownedDomains[0];
      writer.WriteLine($"{BOX_VERTICAL}      Example: CreateOrder -> \"{exampleDomain}.inbox\"                       {BOX_VERTICAL}");
    } else {
      writer.WriteLine($"{BOX_VERTICAL}      Example: CreateOrder -> \"orders.inbox\"                    {BOX_VERTICAL}");
    }

    writer.WriteLine($"{BOX_VERTICAL}                                                                 {BOX_VERTICAL}");
    _renderDivider(writer);

    writer.WriteLine($"{BOX_VERTICAL}                                                                 {BOX_VERTICAL}");
    writer.WriteLine($"{BOX_VERTICAL}  Select option [A/B]: _                                         {BOX_VERTICAL}");
    writer.WriteLine($"{BOX_VERTICAL}                                                                 {BOX_VERTICAL}");
    _renderFooter(writer);

    writer.Write("  Selection: ");
  }

  /// <summary>
  /// Processes user input and updates selection.
  /// </summary>
  /// <param name="input">User input string.</param>
  /// <returns>True if input was valid and processed.</returns>
  public bool ProcessInput(string? input) {
    if (string.IsNullOrWhiteSpace(input)) {
      return false;
    }

    var normalized = input.Trim().ToUpperInvariant();

    switch (normalized) {
      case "A":
        SelectedStrategy = InboxStrategyChoice.SharedTopic;
        return true;

      case "B":
        SelectedStrategy = InboxStrategyChoice.DomainTopics;
        return true;

      default:
        return false;
    }
  }

  /// <summary>
  /// Sets a custom topic name for the SharedTopic strategy.
  /// </summary>
  /// <param name="topic">Custom topic name.</param>
  public void SetCustomTopic(string topic) {
    CustomTopic = topic;
  }

  /// <summary>
  /// Sets a custom suffix for the DomainTopics strategy.
  /// </summary>
  /// <param name="suffix">Custom suffix (e.g., ".in").</param>
  public void SetCustomSuffix(string suffix) {
    CustomSuffix = suffix;
  }

  /// <summary>
  /// Applies the selection to a RoutingDecisions instance.
  /// </summary>
  /// <param name="decisions">The routing decisions to update.</param>
  public void ApplyTo(RoutingDecisions decisions) {
    decisions.InboxStrategy = SelectedStrategy;
    decisions.InboxTopic = CustomTopic;
    decisions.InboxSuffix = CustomSuffix;
  }

  private static void _renderHeader(string title, TextWriter writer) {
    writer.WriteLine($"{BOX_TOP_LEFT}{new string(BOX_HORIZONTAL[0], BOX_WIDTH)}{BOX_TOP_RIGHT}");
    writer.WriteLine($"{BOX_VERTICAL}  {title.PadRight(BOX_WIDTH - 2)}{BOX_VERTICAL}");
    writer.WriteLine($"{BOX_T_LEFT}{new string(BOX_HORIZONTAL[0], BOX_WIDTH)}{BOX_T_RIGHT}");
  }

  private static void _renderDivider(TextWriter writer) {
    writer.WriteLine($"{BOX_T_LEFT}{new string(BOX_HORIZONTAL[0], BOX_WIDTH)}{BOX_T_RIGHT}");
  }

  private static void _renderFooter(TextWriter writer) {
    writer.WriteLine($"{BOX_BOTTOM_LEFT}{new string(BOX_HORIZONTAL[0], BOX_WIDTH)}{BOX_BOTTOM_RIGHT}");
  }
}
