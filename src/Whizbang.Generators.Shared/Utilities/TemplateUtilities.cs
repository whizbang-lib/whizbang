using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Whizbang.Generators.Shared.Utilities;

/// <summary>
/// <tests>tests/Whizbang.Generators.Tests/TemplateUtilitiesTests.cs:ReplaceRegion_WithValidRegion_ReplacesContentAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TemplateUtilitiesTests.cs:ReplaceRegion_WithNonExistentRegion_ReturnsOriginalAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TemplateUtilitiesTests.cs:ReplaceRegion_PreservesIndentationAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TemplateUtilitiesTests.cs:IndentCode_WithNullCode_ReturnsNullAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TemplateUtilitiesTests.cs:IndentCode_WithEmptyCode_ReturnsEmptyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TemplateUtilitiesTests.cs:IndentCode_WithWhitespaceLine_PreservesItAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TemplateUtilitiesTests.cs:IndentCode_WithNonEmptyLines_IndentsThemAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TemplateUtilitiesTests.cs:IndentCode_WithMixedLineEndings_HandlesAllTypesAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TemplateUtilitiesTests.cs:ExtractSnippet_WithNonExistentRegion_ReturnsErrorMessageAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TemplateUtilitiesTests.cs:ExtractSnippet_WithValidRegion_ExtractsContentAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TemplateUtilitiesTests.cs:GetEmbeddedTemplate_WithNonExistentResource_ReturnsErrorAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TemplateUtilitiesTests.cs:GetEmbeddedTemplate_WithValidResource_ReturnsContentAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TemplateUtilitiesTests.cs:ReplaceHeaderRegion_ReplacesTimestampAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TemplateUtilitiesTests.cs:RemoveIndentation_WithNullCode_ReturnsNullAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TemplateUtilitiesTests.cs:RemoveIndentation_WithEmptyIndentation_ReturnsOriginalAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TemplateUtilitiesTests.cs:RemoveIndentation_WithWhitespaceLine_PreservesItAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TemplateUtilitiesTests.cs:RemoveIndentation_WithMatchingIndentation_RemovesItAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TemplateUtilitiesTests.cs:RemoveIndentation_WithNonMatchingIndentation_KeepsLineAsync</tests>
/// Shared utilities for working with source generator templates.
/// Provides robust template marker replacement with indentation preservation.
/// Used by all Whizbang generators to ensure consistent template handling.
/// </summary>
public static class TemplateUtilities {
  // CA1861: Prefer static readonly over constant array arguments for better performance
  private static readonly string[] _lineSeparators = ["\r\n", "\r", "\n"];
  private static readonly char[] _newlineChars = ['\r', '\n'];
  /// <summary>
  /// <para>Replaces a #region block with generated code, preserving indentation.
  /// A string search matches: #region NAME ... #endregion with any content/whitespace between.</para>
  ///
  /// <para>Example:
  /// <code>
  /// var template = "  #region ROUTING\n  // placeholder\n  #endregion";
  /// var code = "if (x) {\n  DoSomething();\n}";
  /// var result = ReplaceRegion(template, "ROUTING", code);
  /// // Result preserves the 2-space indentation from the #region line
  /// </code></para>
  /// </summary>
  /// <param name="template">The template containing #region markers</param>
  /// <param name="regionName">The name of the region to replace (e.g., "SEND_ROUTING")</param>
  /// <param name="replacement">The generated code to insert</param>
  /// <returns>Template with region replaced by generated code, indentation preserved</returns>
  public static string ReplaceRegion(string template, string regionName, string replacement) {
    // Use simple string search instead of regex to avoid catastrophic backtracking
    // This is more efficient for large templates (migration scripts can be 50KB+)
    var regionStart = $"#region {regionName}";
    const string regionEnd = "#endregion";

    var startIdx = template.IndexOf(regionStart, StringComparison.Ordinal);
    if (startIdx < 0) {
      return template;  // Region not found
    }

    var endIdx = template.IndexOf(regionEnd, startIdx, StringComparison.Ordinal);
    if (endIdx < 0) {
      return template;  // No matching #endregion found
    }

    // Capture leading whitespace for indentation (look back from #region)
    var indentStart = _findLineStart(template, startIdx);
    var indentation = template[indentStart..startIdx];

    // Indent the replacement code to match the region's indentation
    var indentedReplacement = IndentCode(replacement.TrimEnd(), indentation);

    // Find boundaries and trailing content
    var replaceEnd = endIdx + regionEnd.Length;
    var trailing = _captureTrailingContent(template, ref replaceEnd);
    _consumeLineEnding(template, ref replaceEnd);

    // Build the result
    var suffix = template[replaceEnd..];
    indentedReplacement = _applyTrailingAndNewline(indentedReplacement, trailing, suffix);

    return template[..indentStart] + indentedReplacement + suffix;
  }

  /// <summary>
  /// Finds the start of the line containing the given position, scanning backward to the
  /// preceding newline character.
  /// </summary>
  private static int _findLineStart(string template, int position) {
    var lineStart = position;
    while (lineStart > 0 && template[lineStart - 1] != '\n' && template[lineStart - 1] != '\r') {
      lineStart--;
    }
    return lineStart;
  }

  /// <summary>
  /// Captures any trailing content after #endregion on the same line (e.g., semicolon).
  /// Advances replaceEnd past the trailing content.
  /// </summary>
  private static string _captureTrailingContent(string template, ref int replaceEnd) {
#pragma warning disable S125 // Descriptive comment with code-like example, not dead code
    // This handles inline patterns like: const string x = #region X ... #endregion;
#pragma warning restore S125
    var trailingContent = new System.Text.StringBuilder();
    while (replaceEnd < template.Length && template[replaceEnd] != '\n' && template[replaceEnd] != '\r') {
      trailingContent.Append(template[replaceEnd]);
      replaceEnd++;
    }
    return trailingContent.ToString();
  }

  /// <summary>
  /// Consumes the line ending at the current position (handles \r\n, \r, or \n).
  /// </summary>
  private static void _consumeLineEnding(string template, ref int position) {
    if (position >= template.Length) {
      return;
    }

    // Callers reach this having stopped at a line break or at the end of the template, so a position
    // holding neither cannot occur today. The check stays as the guard against a future caller that
    // does not stop there — written as a positive block so it is evaluated on every call rather than
    // sitting on an early-return line no input reaches. Anything else is left where it is.
    var isCarriageReturn = template[position] == '\r';
    var isLineFeed = template[position] == '\n';

    if (isCarriageReturn || isLineFeed) {
      position++;

      // Handle \r\n pair
      if (isCarriageReturn && position < template.Length && template[position] == '\n') {
        position++;
      }
    }
  }

  /// <summary>
  /// Applies trailing content and ensures a newline separator if there's a suffix.
  /// </summary>
  private static string _applyTrailingAndNewline(string indentedReplacement, string trailing, string suffix) {
    if (trailing.Length > 0) {
      indentedReplacement = indentedReplacement.TrimEnd() + trailing;
    }

    var needsNewline = suffix.Length > 0 && !_endsWithNewline(indentedReplacement);
    return needsNewline ? indentedReplacement + "\n" : indentedReplacement;
  }

  /// <summary>
  /// Checks whether the string ends with a newline character (\n or \r).
  /// </summary>
  private static bool _endsWithNewline(string value) {
    return value.EndsWith("\n", StringComparison.Ordinal) || value.EndsWith("\r", StringComparison.Ordinal);
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

    var lines = code.Split(_lineSeparators, StringSplitOptions.None);
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

    return ExtractSnippetFrom(GetEmbeddedTemplate(assembly, templateName, resourceNamespace), regionName, templateName);
  }

  /// <summary>
  /// Extracts the contents of a #region block from template text already read, as <see cref="ExtractSnippet"/>
  /// does for an embedded template.
  /// </summary>
  /// <param name="template">The template text</param>
  /// <param name="regionName">The name of the region to extract</param>
  /// <param name="templateName">The template's name, for the message when the region is not found</param>
  /// <returns>The code inside the #region block, without region tags or indentation</returns>
  public static string ExtractSnippetFrom(string template, string regionName, string templateName) {
    // A linear scan, not a regex. The regex this replaces rescanned the whole template lazily on every call
    // under a one-second match timeout, and a generator calls this once per type: on a loaded CI runner the
    // timeout fired, the generator threw RegexMatchTimeoutException, and it emitted nothing at all.
    if (!_tryFindSnippet(template, regionName, out var rawIndentation, out var content)) {
      return $"// ERROR: Snippet region '{regionName}' not found in {templateName}";
    }

    // Remove newline characters from captured indentation
    var indentation = rawIndentation.Replace("\r", "").Replace("\n", "");

    // Remove the base indentation from all lines
    return RemoveIndentation(content, indentation);
  }

  /// <summary>
  /// Finds the first <c>#region</c> named <paramref name="regionName"/> and the code inside it. The rules are
  /// the ones the snippet templates are written to: the whitespace before <c>#region</c> is the indentation,
  /// the rest of the region line is ignored, and the code runs from the line after it to the line break
  /// before the first <c>#endregion</c> that starts a line.
  /// </summary>
  private static bool _tryFindSnippet(string template, string regionName, out string indentation, out string content) {
    const string regionMarker = "#region";
    indentation = "";
    content = "";
    var search = 0;
    int at;
    while ((at = template.IndexOf(regionMarker, search, StringComparison.Ordinal)) >= 0) {
      search = at + regionMarker.Length;
      var lineEnd = _regionLineEnd(template, search, regionName);
      if (lineEnd < 0) {
        continue;
      }
      // At least one line break ends the region line, so the code can start no earlier than after it.
      var lineBreak = _snippetEnd(template, lineEnd + 1);
      if (lineBreak < 0) {
        return false;
      }
      var contentStart = lineEnd;
      while (contentStart < template.Length && _isNewline(template[contentStart])) {
        contentStart++;
      }
      indentation = template[_whitespaceRunStart(template, at, 0)..at];
      content = template[Math.Min(contentStart, lineBreak)..lineBreak];
      return true;
    }
    return false;
  }

  // Where the line of a "#region <regionName>" ends, given the index just past "#region"; -1 when the region is
  // named something else or the line never ends.
  private static int _regionLineEnd(string template, int afterMarker, string regionName) {
    var name = afterMarker;
    while (name < template.Length && char.IsWhiteSpace(template[name])) {
      name++;
    }
    return name == afterMarker || string.CompareOrdinal(template, name, regionName, 0, regionName.Length) != 0
      ? -1
      : template.IndexOfAny(_newlineChars, name + regionName.Length);
  }

  // The line break that ends a snippet: the first one in the whitespace before the first "#endregion" that starts
  // a line, searching from <paramref name="earliest"/>; -1 when there is none.
  private static int _snippetEnd(string template, int earliest) {
    const string endMarker = "#endregion";
    var end = template.IndexOf(endMarker, earliest, StringComparison.Ordinal);
    while (end >= 0) {
      var runStart = _whitespaceRunStart(template, end, earliest);
      var lineBreak = template.IndexOfAny(_newlineChars, runStart, end - runStart);
      if (lineBreak >= 0) {
        return lineBreak;
      }
      end = template.IndexOf(endMarker, end + endMarker.Length, StringComparison.Ordinal);
    }
    return -1;
  }

  // The start of the whitespace run that ends at <paramref name="position"/>, no earlier than <paramref name="floor"/>.
  private static int _whitespaceRunStart(string template, int position, int floor) {
    var start = position;
    while (start > floor && char.IsWhiteSpace(template[start - 1])) {
      start--;
    }
    return start;
  }

  private static bool _isNewline(char c) => c is '\r' or '\n';

  /// <summary>
  /// Removes a specific indentation prefix from each line of code.
  /// Used when extracting snippets to normalize indentation.
  /// </summary>
  public static string RemoveIndentation(string code, string indentationToRemove) {
    if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(indentationToRemove)) {
      return code;
    }

    var lines = code.Split(_lineSeparators, StringSplitOptions.None);
    var result = lines.Select(line => {
      if (string.IsNullOrWhiteSpace(line)) {
        return line;
      }

      if (line.StartsWith(indentationToRemove, StringComparison.Ordinal)) {
        return line[indentationToRemove.Length..];
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
  /// Deterministic replacement for wall-clock generation stamps: the generator
  /// assembly's build version. Wall-clock text baked into generated source changes
  /// the compiler's deterministic output hash (MVID + PDB id) on every build, so a
  /// same-version rebuild would never be byte-identical — which CI's rebuild
  /// verification requires. The generator version still tells a reader which build
  /// of the generator produced the file.
  /// </summary>
  /// <param name="generatorAssembly">The generator assembly whose version stamps the output</param>
  /// <returns>A stable, build-deterministic stamp string</returns>
  public static string GetDeterministicBuildStamp(Assembly generatorAssembly) {
    var version = generatorAssembly.GetName().Version?.ToString() ?? "0.0.0.0";
    return $"generator v{version}";
  }

  /// <summary>
  /// <see cref="GetDeterministicBuildStamp"/> for the assembly containing this class —
  /// after ILRepack merges Shared into each generator package, that IS the generator
  /// assembly. Exists so string interpolations can reference one fully-qualified token.
  /// </summary>
  public static string DeterministicBuildStamp => GetDeterministicBuildStamp(typeof(TemplateUtilities).Assembly);

  /// <summary>
  /// Replaces the HEADER region in a template with a generated header carrying the
  /// deterministic build stamp. Loads the GENERATED_FILE_HEADER snippet and replaces
  /// __TIMESTAMP__ with the stamp from <see cref="GetDeterministicBuildStamp"/> —
  /// never wall-clock time, which would break build determinism.
  /// </summary>
  /// <param name="assembly">The assembly containing the embedded resources</param>
  /// <param name="template">The template content with a HEADER region</param>
  /// <returns>Template with HEADER region replaced with the stamped header</returns>
  public static string ReplaceHeaderRegion(Assembly assembly, string template) {
    var timestamp = GetDeterministicBuildStamp(assembly);

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
