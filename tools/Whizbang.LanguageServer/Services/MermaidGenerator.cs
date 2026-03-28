using System.Text;

namespace Whizbang.LanguageServer.Services;

/// <summary>
/// Generates Mermaid flowchart diagrams from message registry data.
/// Shows the dispatch flow: Dispatchers → Message → Receptors + Perspectives.
/// </summary>
public sealed class MermaidGenerator {
    /// <summary>
    /// Generates a Mermaid flowchart for a message showing its full dispatch flow.
    /// </summary>
    /// <param name="messageType">The message type name (e.g., "CreateOrderCommand").</param>
    /// <param name="isCommand">Whether the message is a command.</param>
    /// <param name="isEvent">Whether the message is an event.</param>
    /// <param name="dispatchers">List of (className, methodName) tuples that dispatch this message.</param>
    /// <param name="receptors">List of (className, methodName) tuples that handle this message.</param>
    /// <param name="perspectives">List of class names that observe this message.</param>
    /// <returns>Mermaid diagram code string.</returns>
    public string Generate(
        string messageType,
        bool isCommand,
        bool isEvent,
        IReadOnlyList<(string ClassName, string MethodName)> dispatchers,
        IReadOnlyList<(string ClassName, string MethodName)> receptors,
        IReadOnlyList<string> perspectives) {
        var sb = new StringBuilder();
        sb.AppendLine("graph LR");

        var msgId = SanitizeId(messageType);
        var msgLabel = messageType;
        var msgShape = isCommand ? $"[/{msgLabel}\\]" : isEvent ? $"(({msgLabel}))" : $"[{msgLabel}]";

        // Message node
        sb.AppendLine($"    {msgId}{msgShape}");

        // Dispatchers
        if (dispatchers.Count > 0) {
            sb.AppendLine();
            sb.AppendLine("    subgraph Dispatchers");
            for (var i = 0; i < dispatchers.Count; i++) {
                var (cls, method) = dispatchers[i];
                var id = $"D{i}";
                var label = string.IsNullOrEmpty(method) ? cls : $"{cls}.{method}";
                sb.AppendLine($"        {id}[\"{label}\"]");
            }
            sb.AppendLine("    end");

            for (var i = 0; i < dispatchers.Count; i++) {
                sb.AppendLine($"    D{i} --> {msgId}");
            }
        }

        // Receptors
        if (receptors.Count > 0) {
            sb.AppendLine();
            sb.AppendLine("    subgraph Receptors");
            for (var i = 0; i < receptors.Count; i++) {
                var (cls, method) = receptors[i];
                var id = $"R{i}";
                var label = string.IsNullOrEmpty(method) ? cls : $"{cls}.{method}";
                sb.AppendLine($"        {id}[\"{label}\"]");
            }
            sb.AppendLine("    end");

            for (var i = 0; i < receptors.Count; i++) {
                sb.AppendLine($"    {msgId} --> R{i}");
            }
        }

        // Perspectives (events only)
        if (perspectives.Count > 0) {
            sb.AppendLine();
            sb.AppendLine("    subgraph Perspectives");
            for (var i = 0; i < perspectives.Count; i++) {
                var id = $"P{i}";
                sb.AppendLine($"        {id}[\"{perspectives[i]}\"]");
            }
            sb.AppendLine("    end");

            for (var i = 0; i < perspectives.Count; i++) {
                sb.AppendLine($"    {msgId} --> P{i}");
            }
        }

        // Styling
        sb.AppendLine();
        sb.AppendLine($"    style {msgId} fill:#4fc3f7,stroke:#0288d1,color:#000");
        if (dispatchers.Count > 0) {
            for (var i = 0; i < dispatchers.Count; i++) sb.AppendLine($"    style D{i} fill:#a5d6a7,stroke:#388e3c,color:#000");
        }
        if (receptors.Count > 0) {
            for (var i = 0; i < receptors.Count; i++) sb.AppendLine($"    style R{i} fill:#ffcc80,stroke:#f57c00,color:#000");
        }
        if (perspectives.Count > 0) {
            for (var i = 0; i < perspectives.Count; i++) sb.AppendLine($"    style P{i} fill:#ce93d8,stroke:#7b1fa2,color:#000");
        }

        return sb.ToString().TrimEnd();
    }

    private static string SanitizeId(string name) =>
        name.Replace(".", "_").Replace("<", "_").Replace(">", "_").Replace(",", "_").Replace(" ", "_");
}
