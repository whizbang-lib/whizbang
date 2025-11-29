using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Whizbang.Generators.Shared.Utilities;

/// <summary>
/// Shared utilities for working with source generator templates.
/// Provides robust template marker replacement with indentation preservation.
/// Used by all Whizbang generators to ensure consistent template handling.
/// </summary>
public static class TemplateUtilities {
  /// <summary>
  /// Replaces a #region block with generated code, preserving indentation.
  /// Regex pattern matches: #region NAME ... #endregion with any content/whitespace between.
  ///
  /// Example:
  /// <code>
  /// var template = "  #region ROUTING\n  // placeholder\n  #endregion";
  /// var code = "if (x) {\n  DoSomething();\n}";
  /// var result = ReplaceRegion(template, "ROUTING", code);
  /// // Result preserves the 2-space indentation from the #region line
  /// </code>
  /// </summary>
  /// <param name="template">The template containing #region markers</param>
  /// <param name="regionName">The name of the region to replace (e.g., "SEND_ROUTING")</param>
  /// <param name="replacement">The generated code to insert</param>
  /// <returns>Template with region replaced by generated code, indentation preserved</returns>
  public static string ReplaceRegion(string template, string regionName, string replacement) {
    // Pattern explanation:
    // (\s*)           - Capture leading whitespace (for indentation preservation)
    // #region\s+      - Match '#region' followed by whitespace
    // {regionName}    - Match the specific region name
    // \s*             - Optional whitespace after region name
    // (?:[^\r\n]*)    - Match rest of line (non-capturing, allows region description)
    // [\r\n]+         - Match line ending(s)
    // .*?             - Match any content between (non-greedy)
    // \s*             - Optional whitespace before endregion
    // #endregion      - Match '#endregion'
    var pattern = $@"(\s*)#region\s+{Regex.Escape(regionName)}\s*(?:[^\r\n]*)[\r\n]+.*?\s*#endregion";

    var match = Regex.Match(template, pattern, RegexOptions.Singleline);
    if (!match.Success) {
      // Fallback: region not found, return original
      return template;
    }

    // Get the indentation from the captured group
    var indentation = match.Groups[1].Value;

    // Indent the replacement code to match the region's indentation
    var indentedReplacement = IndentCode(replacement.TrimEnd(), indentation);

    // Replace the entire region block with the indented code
    return Regex.Replace(template, pattern, indentedReplacement, RegexOptions.Singleline);
  }

  /// <summary>
  /// Indents each line of code with the specified indentation string.
  /// Empty lines are preserved without indentation.
  /// </summary>
  /// <param name="code">The code to indent</param>
  /// <param name="indentation">The indentation string (e.g., "  " or "    ")</param>
  /// <returns>Code with each non-empty line indented</returns>
  public static string IndentCode(string code, string indentation) {
    if (string.IsNullOrEmpty(code)) {
      return code;
    }

    var lines = code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
    var indentedLines = lines.Select(line =>
        string.IsNullOrWhiteSpace(line) ? line : indentation + line
    );

    return string.Join("\n", indentedLines);
  }

  /// <summary>
  /// Extracts the contents of a #region block from a template file.
  /// Used for snippet-based code generation where small code blocks are extracted and reused.
  /// </summary>
  /// <param name="assembly">The assembly containing the embedded resource</param>
  /// <param name="templateName">Template filename (e.g., "DispatcherSnippets.cs")</param>
  /// <param name="regionName">The name of the region to extract (e.g., "SEND_ROUTING_SNIPPET")</param>
  /// <param name="resourceNamespace">Namespace prefix for resources (default: "Whizbang.Generators.Templates.Snippets")</param>
  /// <returns>The code inside the #region block, without region tags or indentation</returns>
  public static string ExtractSnippet(
      Assembly assembly,
      string templateName,
      string regionName,
      string resourceNamespace = "Whizbang.Generators.Templates.Snippets") {

    var template = GetEmbeddedTemplate(assembly, templateName, resourceNamespace);

    // Pattern to extract content between #region and #endregion
    var pattern = $@"(\s*)#region\s+{Regex.Escape(regionName)}[^\r\n]*[\r\n]+(.*?)[\r\n]+\s*#endregion";

    var match = Regex.Match(template, pattern, RegexOptions.Singleline);
    if (!match.Success) {
      return $"// ERROR: Snippet region '{regionName}' not found in {templateName}";
    }

    var rawIndentation = match.Groups[1].Value;
    var content = match.Groups[2].Value;

    // Remove newline characters from captured indentation
    var indentation = rawIndentation.Replace("\r", "").Replace("\n", "");

    // Remove the base indentation from all lines
    return RemoveIndentation(content, indentation);
  }

  /// <summary>
  /// Removes a specific indentation prefix from each line of code.
  /// Used when extracting snippets to normalize indentation.
  /// </summary>
  private static string RemoveIndentation(string code, string indentationToRemove) {
    if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(indentationToRemove)) {
      return code;
    }

    var lines = code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
    var result = lines.Select(line => {
      if (string.IsNullOrWhiteSpace(line)) {
        return line;
      }

      if (line.StartsWith(indentationToRemove)) {
        return line.Substring(indentationToRemove.Length);
      }
      return line;
    });

    return string.Join("\n", result);
  }

  /// <summary>
  /// Reads an embedded template file from the Templates directory.
  /// </summary>
  /// <param name="assembly">The assembly containing the embedded resource</param>
  /// <param name="templateName">Template filename (e.g., "DispatcherTemplate.cs")</param>
  /// <param name="resourceNamespace">Namespace prefix for resources (default: "Whizbang.Generators.Templates")</param>
  /// <returns>Template file contents, or error message if not found</returns>
  public static string GetEmbeddedTemplate(
      Assembly assembly,
      string templateName,
      string resourceNamespace = "Whizbang.Generators.Templates") {

    var resourceName = $"{resourceNamespace}.{templateName}";

    using var stream = assembly.GetManifestResourceStream(resourceName);
    if (stream == null) {
      // Fallback: if embedded resource not found, return empty template
      return "// ERROR: Template not found: " + templateName;
    }

    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
  }

  /// <summary>
  /// Replaces the HEADER region in a template with a generated header containing timestamp.
  /// Loads the GENERATED_FILE_HEADER snippet and replaces __TIMESTAMP__ with current UTC time.
  /// </summary>
  /// <param name="assembly">The assembly containing the embedded resources</param>
  /// <param name="template">The template content with a HEADER region</param>
  /// <returns>Template with HEADER region replaced with timestamped header</returns>
  public static string ReplaceHeaderRegion(Assembly assembly, string template) {
    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC");

    // Load header snippet
    var headerSnippet = ExtractSnippet(
        assembly,
        "DispatcherSnippets.cs",
        "GENERATED_FILE_HEADER"
    );

    // Replace timestamp placeholder in header
    var header = headerSnippet.Replace("__TIMESTAMP__", timestamp);

    // Replace HEADER region in template
    return ReplaceRegion(template, "HEADER", header);
  }
}
