using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Diagnostics;

/// <summary>
/// Renders the Whizbang ASCII art banner with true-color ANSI escape codes.
/// Used by CLI tools on startup and optionally by the Whizbang host on service start.
/// Banner data (characters + colors) is generated from canonical source files
/// by Build-Logo.ps1 into WhizbangBanner.Generated.cs.
/// </summary>
/// <docs>operations/observability/diagnostics</docs>
public static partial class WhizbangBanner {
  private const string ESC_CODE = "\x1b";
  private const int BACKGROUND_R = 45;
  private const int BACKGROUND_G = 55;
  private const int BACKGROUND_B = 72;
  private const int BANNER_WIDTH = 84;

  private static readonly string _background = $"{ESC_CODE}[48;2;{BACKGROUND_R};{BACKGROUND_G};{BACKGROUND_B}m";
  private static readonly string _reset = $"{ESC_CODE}[0m";
  private static readonly char[] _starChars = ['.', '·', '∙', '*', '⋅', '✦'];

  /// <summary>
  /// Gets whether the terminal supports ANSI color escape codes.
  /// Checks for output redirection, CI environment variables, TERM/COLORTERM,
  /// and known terminal programs (Windows Terminal, VS Code).
  /// </summary>
  public static bool SupportsAnsiColor {
    get {
      if (Console.IsOutputRedirected) {
        return false;
      }

      // COLORTERM=truecolor or 24bit is definitive
      var colorTerm = Environment.GetEnvironmentVariable("COLORTERM");
      if (colorTerm is "truecolor" or "24bit") {
        return true;
      }

      // CI environments that render ANSI colors in their log output
      if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) ||
          !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD"))) {
        return true;
      }

      // TERM set to anything other than "dumb" indicates color support
      var term = Environment.GetEnvironmentVariable("TERM");
      if (!string.IsNullOrEmpty(term) && term != "dumb") {
        return true;
      }

      // Known color-capable terminal programs
      if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION")) ||
          Environment.GetEnvironmentVariable("TERM_PROGRAM") == "vscode") {
        return true;
      }

      // Modern Windows consoles (Win10+) and all Unix terminals support ANSI
      return Environment.UserInteractive;
    }
  }

  /// <summary>
  /// Writes the Whizbang ASCII art banner to the console with true-color ANSI gradient
  /// and random star decorations on a dark navy background.
  /// Falls back to plain text when the terminal does not support ANSI color.
  /// </summary>
  /// <param name="writer">TextWriter to output to. Defaults to Console.Out.</param>
  /// <param name="enabled">When false, the banner is not printed. Allows config-driven control.</param>
  public static void Print(TextWriter? writer = null, bool enabled = true) {
    if (!enabled) {
      return;
    }

    writer ??= Console.Out;

    // Fall back to plain text when the terminal lacks ANSI color support
    if (!SupportsAnsiColor) {
      _printPlain(writer);
      return;
    }

    var random = Random.Shared;
    var sb = new StringBuilder(4096);
    var colors = _colorData;

    sb.AppendLine();

    for (var row = 0; row < BANNER_ROWS; row++) {
      var line = _plainBanner[row];
      var col = 0;

      // Walk columns, coalescing adjacent same-color characters into segments
      while (col < BANNER_WIDTH) {
        var idx = (row * BANNER_WIDTH + col) * 3;
        var r = colors[idx];
        var g = colors[idx + 1];
        var b = colors[idx + 2];

        // Find run of same color
        var runStart = col;
        while (col < BANNER_WIDTH) {
          var nextIdx = (row * BANNER_WIDTH + col) * 3;
          if (colors[nextIdx] != r || colors[nextIdx + 1] != g || colors[nextIdx + 2] != b) {
            break;
          }
          col++;
        }

        var text = line[runStart..col];
        _appendSegment(sb, text, r, g, b, random);
      }

      _appendEndOfLine(sb);
    }

    sb.AppendLine();
    writer.Write(sb);
  }

  /// <summary>
  /// Writes the plain text banner (no ANSI codes) to the specified writer.
  /// Used as a fallback when the terminal does not support ANSI color.
  /// </summary>
  /// <param name="writer">TextWriter to output to.</param>
  private static void _printPlain(TextWriter writer) {
    foreach (var line in _plainBanner) {
      writer.WriteLine(line);
    }
  }

  private static void _appendSegment(StringBuilder sb, string text, int r, int g, int b, Random random) {
    var isBg = r == BACKGROUND_R && g == BACKGROUND_G && b == BACKGROUND_B;
    foreach (var ch in text) {
      if (isBg && ch == ' ' && random.Next(12) == 0) {
        _appendStarChar(sb, random);
      } else {
        sb.Append(CultureInfo.InvariantCulture, $"{_background}{ESC_CODE}[38;2;{r};{g};{b}m{ch}{_reset}");
      }
    }
  }

  private static void _appendStarChar(StringBuilder sb, Random random) {
    var brightness = random.Next(220, 256);
    var star = _starChars[random.Next(_starChars.Length)];
    sb.Append(CultureInfo.InvariantCulture, $"{_background}{ESC_CODE}[38;2;{brightness};{brightness + 5};{brightness + 10}m{star}{_reset}");
  }

  private static void _appendEndOfLine(StringBuilder sb) {
    sb.Append(CultureInfo.InvariantCulture, $"{_background}  {_reset}");
    sb.AppendLine();
  }

  /// <summary>
  /// Prints the full branded header: ASCII art banner + config box.
  /// For CLI tools, pass the tool name and version. For services, pass the service name
  /// and optionally the Whizbang library version.
  /// </summary>
  /// <param name="name">Display name (e.g., "PR Runner", "OrderService", "whizbang-migrate").</param>
  /// <param name="version">Version string (e.g., "1.0.0"). If null, reads from the calling assembly.</param>
  /// <param name="parameters">Key-value pairs to display (e.g., Mode, Action, Branch).</param>
  /// <param name="whizbangVersion">Whizbang library version. If null, reads from Whizbang.Core assembly. Shown for services.</param>
  /// <param name="enabled">When false, nothing is printed.</param>
  public static void PrintHeader(
      string name,
      string? version = null,
      IDictionary<string, string>? parameters = null,
      string? whizbangVersion = null,
      bool enabled = true) {
    if (!enabled) {
      return;
    }

    // Auto-detect versions if not provided (compile-time constant, no reflection)
    version ??= "0.0.0";
    whizbangVersion ??= WhizbangVersionInfo.Version;

    // Print the ASCII art banner (auto-detects color support)
    Print();

    // Build config line
    var configParts = new List<string>();
    if (parameters != null) {
      foreach (var kvp in parameters.OrderBy(k => k.Key)) {
        configParts.Add($"{kvp.Key}: {kvp.Value}");
      }
    }

    var configLine = string.Join(" | ", configParts);

    // Fixed width matching the logo banner
    const int innerWidth = BANNER_WIDTH - 4;

    var titleLine = whizbangVersion != null
        ? $"  {name} v{version} (Whizbang v{whizbangVersion})"
        : $"  {name} v{version}";

    Console.WriteLine($"  ╔{new string('═', innerWidth)}╗");
    Console.WriteLine($"  ║{titleLine.PadRight(innerWidth)}║");

    if (!string.IsNullOrEmpty(configLine)) {
      Console.WriteLine($"  ║{"  " + configLine.PadRight(innerWidth - 2)}║");
    }

    Console.WriteLine($"  ╚{new string('═', innerWidth)}╝");
    Console.WriteLine();
  }

  /// <summary>
  /// Logs the Whizbang banner with full ANSI true-color via ILogger.
  /// Use with Serilog console sink or other terminal-aware sinks that preserve ANSI codes.
  /// </summary>
  /// <param name="logger">The logger to write the banner to.</param>
  /// <param name="enabled">When false, the banner is not logged. Allows config-driven control.</param>
  public static void LogBannerAnsi(ILogger logger, bool enabled = true) {
    if (!enabled || !logger.IsEnabled(LogLevel.Information)) {
      return;
    }

    using var sw = new StringWriter();
    Print(sw);
    var bannerText = sw.ToString();
    LogBannerLine(logger, bannerText);
  }

  /// <summary>
  /// Logs the Whizbang banner as plain text (no ANSI codes) via ILogger.
  /// Use with file loggers, structured logging sinks (Seq, Application Insights),
  /// or any sink that strips ANSI escape codes.
  /// </summary>
  /// <param name="logger">The logger to write the banner to.</param>
  /// <param name="enabled">When false, the banner is not logged. Allows config-driven control.</param>
  public static void LogBanner(ILogger logger, bool enabled = true) {
    if (!enabled) {
      return;
    }

    foreach (var line in _plainBanner) {
      LogBannerLine(logger, line);
    }
  }

  [LoggerMessage(Level = LogLevel.Information, Message = "{BannerLine}")]
  private static partial void LogBannerLine(ILogger logger, string bannerLine);
}
