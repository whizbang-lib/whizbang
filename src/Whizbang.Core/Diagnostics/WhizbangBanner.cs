using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Diagnostics;

/// <summary>
/// Renders the Whizbang ASCII art banner with true-color ANSI escape codes.
/// Used by CLI tools on startup and optionally by the Whizbang host on service start.
/// </summary>
/// <docs>core-concepts/diagnostics</docs>
public static partial class WhizbangBanner {
  private const string ESC_CODE = "\x1b";
  private const int BACKGROUND_R = 45;
  private const int BACKGROUND_G = 55;
  private const int BACKGROUND_B = 72;

  private static readonly string _background = $"{ESC_CODE}[48;2;{BACKGROUND_R};{BACKGROUND_G};{BACKGROUND_B}m";
  private static readonly string _reset = $"{ESC_CODE}[0m";
  private static readonly char[] _starChars = ['.', '·', '∙', '*', '⋅', '✦'];

  private static readonly string[] _plainBanner =
  [
      "",
        "  Φ▌▌     ,▄▄         ▌▌H      ╒██⌐         ▓▓L",
        "   ██W   █████    ▄▄m ▓█▄▄▌▌▄   ▄▄  ▄▄▄▄▄▄╕ ██▌▌▌▌▄_   ,▄▌▌▄▄▄⌐ ╔▄▄▄▌▌▄    ²▌▌▌▄▄▄",
        "   ▀██  ▌██ ╟██  ▐▓▓  ▓█▓\"'▀██  ██  \"\"╠▓▓▀  ███╙\"╨██╕▄██▀╙╙▀██M ▓██▀²▀██ ┌██▀\"╙▓██",
        "    ██▄▄██   ██▌_▓▓Ñ  ▓█H   ██  ██  _Φ▓▌    ██▌   ▄▓██▌▓▄   ██M ╫▓▌   ██ ▐██   ╓██",
        "    ╙████     ▀██▓▀   ▓█M   ██  ██ ▐▓▓▓▓▓▓▌ ███████▌\" '▓██████M ▓█▌   ██  ╨███████",
        "                                                                          ▓█▌▄▄▓█▌",
        "",
        "                                W! - https://whizba.ng/",
        "",
    ];

  /// <summary>
  /// Writes the Whizbang ASCII art banner to the console with true-color ANSI gradient
  /// and random star decorations on a dark navy background.
  /// </summary>
  /// <param name="writer">TextWriter to output to. Defaults to Console.Out.</param>
  /// <param name="enabled">When false, the banner is not printed. Allows config-driven control.</param>
  public static void Print(TextWriter? writer = null, bool enabled = true) {
    if (!enabled) {
      return;
    }

    writer ??= Console.Out;
    var random = Random.Shared;
    var sb = new StringBuilder(4096);

    void Seg(string text, int r, int g, int b) {
      var isBg = r == BACKGROUND_R && g == BACKGROUND_G && b == BACKGROUND_B;
      foreach (var ch in text) {
        if (isBg && ch == ' ' && random.Next(12) == 0) {
          var brightness = random.Next(220, 256);
          var star = _starChars[random.Next(_starChars.Length)];
          sb.Append(CultureInfo.InvariantCulture, $"{_background}{ESC_CODE}[38;2;{brightness};{brightness + 5};{brightness + 10}m{star}{_reset}");
        } else {
          sb.Append(CultureInfo.InvariantCulture, $"{_background}{ESC_CODE}[38;2;{r};{g};{b}m{ch}{_reset}");
        }
      }
    }

    void Eol() {
      sb.Append(CultureInfo.InvariantCulture, $"{_background}  {_reset}");
      sb.AppendLine();
    }

    sb.AppendLine();

    // Background line above
    Seg("                                                                                    ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Eol();

    // Line 1
    Seg("  ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("Φ", 70, 158, 174); Seg("▌", 56, 155, 181); Seg("▌     ", 57, 144, 176);
    Seg(",▄▄", 108, 101, 131);
    Seg("         ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("▌▌", 190, 60, 105); Seg("H", 154, 100, 108);
    Seg("      ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("╒", 144, 126, 110); Seg("██", 234, 124, 16); Seg("⌐", 148, 129, 106);
    Seg("         ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("▓▓", 150, 152, 154); Seg("L", 165, 167, 169);
    Seg("                                     ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Eol();

    // Line 2
    Seg("   ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("██", 19, 161, 206); Seg("W", 94, 128, 148); Seg("   ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("█████", 66, 52, 143);
    Seg("    ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("▄▄", 173, 70, 133); Seg("m ", 161, 92, 125);
    Seg("▓█", 210, 42, 88); Seg("▄▄▌▌▄", 175, 90, 70);
    Seg("   ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("▄▄", 186, 131, 66);
    Seg("  ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("▄▄▄▄▄▄", 181, 146, 71); Seg("╕", 158, 138, 95);
    Seg(" ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("██▌▌▌▌▄", 140, 142, 144); Seg("_", 170, 170, 170);
    Seg("   ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg(",▄▌▌▄▄▄⌐", 155, 157, 159);
    Seg(" ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("╔▄▄▄▌▌▄", 155, 157, 159);
    Seg("    ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("²▌▌▌▄▄▄", 150, 152, 154);
    Seg("  ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Eol();

    // Line 3
    Seg("   ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("▀", 53, 142, 178); Seg("██", 24, 131, 191);
    Seg("  ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("▌", 80, 89, 141); Seg("██", 45, 45, 143);
    Seg(" ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("╟", 115, 84, 134); Seg("██", 121, 36, 141);
    Seg("  ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("▐▓▓", 156, 90, 131);
    Seg("  ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("▓█▓", 208, 43, 62); Seg("\"", 160, 101, 94); Seg("'", 157, 110, 97);
    Seg("▀██", 195, 100, 55);
    Seg("  ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("██", 239, 130, 11);
    Seg("  ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("\"\"", 172, 143, 80); Seg("╠▓▓", 187, 165, 64); Seg("▀", 213, 157, 36);
    Seg("  ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("███", 140, 142, 144); Seg("╙", 160, 162, 164); Seg("\"", 165, 167, 169); Seg("╨██", 135, 137, 139);
    Seg("╕", 165, 167, 169);
    Seg("▄██▀", 138, 140, 142); Seg("╙╙", 165, 167, 169); Seg("▀██", 130, 132, 134); Seg("M", 160, 162, 164);
    Seg(" ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("▓██▀", 145, 147, 149); Seg("²", 165, 167, 169); Seg("▀██", 130, 132, 134);
    Seg(" ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("┌██▀", 170, 172, 174); Seg("\"", 165, 167, 169); Seg("╙▓██", 145, 147, 149);
    Seg("  ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Eol();

    // Line 4
    Seg("    ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("██", 27, 102, 180); Seg("▄▄", 97, 113, 140); Seg("██", 42, 54, 147);
    Seg("   ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("██▌", 132, 56, 137); Seg("_", 132, 122, 128); Seg("▓▓", 205, 26, 137); Seg("Ñ", 181, 71, 123);
    Seg("  ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("▓█", 206, 44, 55); Seg("H", 165, 98, 89);
    Seg("   ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("██", 239, 103, 12);
    Seg("  ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("██", 239, 143, 10);
    Seg("  ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("_", 137, 131, 117); Seg("Φ▓▌", 199, 166, 52);
    Seg("    ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("██▌", 140, 142, 144);
    Seg("   ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("▄▓██▌▓▄", 143, 145, 147);
    Seg("   ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("██M", 138, 140, 142);
    Seg(" ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("╫▓▌", 155, 157, 159);
    Seg("   ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("██", 130, 132, 134);
    Seg(" ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("▐██", 160, 162, 164);
    Seg("   ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("╓██", 170, 172, 174);
    Seg("  ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Eol();

    // Line 5
    Seg("    ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("╙", 99, 122, 142); Seg("████", 35, 81, 157);
    Seg("     ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("▀██▓▀", 152, 49, 137);
    Seg("   ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("▓█", 204, 47, 51); Seg("M", 167, 98, 87);
    Seg("   ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("██", 239, 108, 12);
    Seg("  ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("██", 239, 148, 10);
    Seg(" ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("▐▓▓▓▓▓▓▌", 200, 180, 80);
    Seg(" ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("███████▌", 140, 142, 144); Seg("\"", 165, 167, 169);
    Seg(" ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("'", 170, 172, 174); Seg("▓██████", 143, 145, 147); Seg("M", 160, 162, 164);
    Seg(" ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("▓█▌", 210, 212, 214);
    Seg("   ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("██", 130, 132, 134);
    Seg("  ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("╨███████", 138, 140, 142);
    Seg("  ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Eol();

    // Line 6: g descender
    Seg("                                                                          ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("▓█▌▄▄▓█▌", 150, 152, 154);
    Seg("  ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Eol();

    // Background line below
    Seg("                                                                                    ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Eol();

    // W! - https://whizba.ng/ tagline
    Seg("                                ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Seg("W! - https://whizba.ng/", 200, 210, 220);
    Seg("                               ", BACKGROUND_R, BACKGROUND_G, BACKGROUND_B);
    Eol();

    sb.AppendLine();
    writer.Write(sb);
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
