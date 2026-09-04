using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;

namespace Whizbang.Core.Tests.Diagnostics;

/// <summary>
/// Covers <see cref="WhizbangBanner"/>'s colour detection and both render paths.
/// </summary>
/// <remarks>
/// Detection reads process-wide environment variables, and the render path is gated on
/// it, so this class serialises on its own key and snapshots every variable it touches
/// in <see cref="Save"/>/<see cref="Restore"/>. Leaking COLORTERM or TERM into a
/// sibling test would change what an unrelated banner assertion sees.
/// </remarks>
[NotInParallel("WhizbangBannerEnvironment")]
[Category("Core")]
[Category("Diagnostics")]
public class WhizbangBannerTests {
  private static readonly string[] _vars =
    ["COLORTERM", "CI", "TF_BUILD", "TERM", "WT_SESSION", "TERM_PROGRAM"];

  private readonly Dictionary<string, string?> _saved = [];

  [Before(Test)]
  public void Save() {
    foreach (var v in _vars) {
      _saved[v] = Environment.GetEnvironmentVariable(v);
      Environment.SetEnvironmentVariable(v, null);
    }
    WhizbangBanner.OutputRedirectedOverride = false;
  }

  [After(Test)]
  public void Restore() {
    foreach (var (k, v) in _saved) {
      Environment.SetEnvironmentVariable(k, v);
    }
    WhizbangBanner.OutputRedirectedOverride = null;
  }

  // --- Colour detection ------------------------------------------------------

  [Test]
  public async Task SupportsAnsiColor_WhenOutputIsRedirected_IsFalseAsync() {
    // Redirected output is a pipe or a file; colour codes there are noise.
    WhizbangBanner.OutputRedirectedOverride = true;
    Environment.SetEnvironmentVariable("COLORTERM", "truecolor");

    await Assert.That(WhizbangBanner.SupportsAnsiColor).IsFalse();
  }

  [Test]
  [Arguments("truecolor")]
  [Arguments("24bit")]
  public async Task SupportsAnsiColor_WithColorTerm_IsTrueAsync(string value) {
    Environment.SetEnvironmentVariable("COLORTERM", value);

    await Assert.That(WhizbangBanner.SupportsAnsiColor).IsTrue();
  }

  [Test]
  public async Task SupportsAnsiColor_WithUnknownColorTerm_FallsThroughAsync() {
    Environment.SetEnvironmentVariable("COLORTERM", "somethingelse");
    Environment.SetEnvironmentVariable("TERM", "xterm-256color");

    await Assert.That(WhizbangBanner.SupportsAnsiColor).IsTrue();
  }

  [Test]
  [Arguments("CI")]
  [Arguments("TF_BUILD")]
  public async Task SupportsAnsiColor_OnCiRunners_IsTrueAsync(string variable) {
    Environment.SetEnvironmentVariable(variable, "true");

    await Assert.That(WhizbangBanner.SupportsAnsiColor).IsTrue();
  }

  [Test]
  public async Task SupportsAnsiColor_WithTermSet_IsTrueAsync() {
    Environment.SetEnvironmentVariable("TERM", "xterm-256color");

    await Assert.That(WhizbangBanner.SupportsAnsiColor).IsTrue();
  }

  [Test]
  public async Task SupportsAnsiColor_WithDumbTerm_FallsThroughAsync() {
    // "dumb" is the conventional way to say the terminal renders nothing.
    Environment.SetEnvironmentVariable("TERM", "dumb");
    Environment.SetEnvironmentVariable("WT_SESSION", "abc");

    await Assert.That(WhizbangBanner.SupportsAnsiColor).IsTrue();
  }

  [Test]
  public async Task SupportsAnsiColor_WithWindowsTerminalSession_IsTrueAsync() {
    Environment.SetEnvironmentVariable("WT_SESSION", "some-guid");

    await Assert.That(WhizbangBanner.SupportsAnsiColor).IsTrue();
  }

  [Test]
  public async Task SupportsAnsiColor_InVsCodeTerminal_IsTrueAsync() {
    Environment.SetEnvironmentVariable("TERM_PROGRAM", "vscode");

    await Assert.That(WhizbangBanner.SupportsAnsiColor).IsTrue();
  }

  [Test]
  public async Task SupportsAnsiColor_WithNoSignals_FallsBackToUserInteractiveAsync() {
    // Last resort: modern Windows consoles and all Unix terminals handle ANSI, so the
    // answer is whatever UserInteractive says. Assert it agrees rather than pinning a
    // value that depends on how the run was launched.
    await Assert.That(WhizbangBanner.SupportsAnsiColor).IsEqualTo(Environment.UserInteractive);
  }

  // --- Rendering -------------------------------------------------------------

  [Test]
  public async Task Print_WhenDisabled_WritesNothingAsync() {
    using var writer = new StringWriter();

    WhizbangBanner.Print(writer, enabled: false);

    await Assert.That(writer.ToString()).IsEmpty();
  }

  [Test]
  public async Task Print_WithoutColorSupport_WritesThePlainBannerAsync() {
    WhizbangBanner.OutputRedirectedOverride = true;
    using var writer = new StringWriter();

    WhizbangBanner.Print(writer);

    var output = writer.ToString();
    await Assert.That(output).IsNotEmpty();
    await Assert.That(output).DoesNotContain("\x1b[");
  }

  [Test]
  public async Task Print_WithColorSupport_EmitsAnsiSequencesAsync() {
    Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
    using var writer = new StringWriter();

    WhizbangBanner.Print(writer);

    var output = writer.ToString();
    await Assert.That(output).IsNotEmpty();
    await Assert.That(output).Contains("\x1b[");
    await Assert.That(output).Contains("\x1b[0m");
  }

  [Test]
  public async Task Print_WithColorSupport_SetsTheBannerBackgroundAsync() {
    Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
    using var writer = new StringWriter();

    WhizbangBanner.Print(writer);

    await Assert.That(writer.ToString()).Contains("\x1b[48;2;45;55;72m");
  }

  [Test]
  public async Task Print_WithColorSupport_RendersEveryBannerRowAsync() {
    Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
    using var writer = new StringWriter();

    WhizbangBanner.Print(writer);

    // A leading blank line plus one line per banner row.
    var lines = writer.ToString().Split('\n');
    await Assert.That(lines.Length).IsGreaterThan(1);
  }

  [Test]
  public async Task Print_IsDeterministicInShapeAcrossRunsAsync() {
    // The star glyphs are random, so two runs differ in content but not in row count.
    Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
    using var first = new StringWriter();
    using var second = new StringWriter();

    WhizbangBanner.Print(first);
    WhizbangBanner.Print(second);

    await Assert.That(first.ToString().Split('\n').Length)
        .IsEqualTo(second.ToString().Split('\n').Length);
  }

  // --- Logger overloads ------------------------------------------------------

  [Test]
  public async Task LogBannerAnsi_WhenDisabled_LogsNothingAsync() {
    var logger = new RecordingLogger();

    WhizbangBanner.LogBannerAnsi(logger, enabled: false);

    await Assert.That(logger.Entries).IsEmpty();
  }

  [Test]
  public async Task LogBannerAnsi_WhenInformationIsOff_LogsNothingAsync() {
    // The banner is decoration; it must not cost a string build when the level is off.
    var logger = new RecordingLogger { Enabled = false };

    WhizbangBanner.LogBannerAnsi(logger);

    await Assert.That(logger.Entries).IsEmpty();
  }

  [Test]
  public async Task LogBanner_WhenDisabled_LogsNothingAsync() {
    var logger = new RecordingLogger();

    WhizbangBanner.LogBanner(logger, enabled: false);

    await Assert.That(logger.Entries).IsEmpty();
  }

  // --- PrintHeader -----------------------------------------------------------

  [Test]
  public async Task PrintHeader_WhenDisabled_WritesNothingAsync() {
    // The header is config-driven; a service that turns it off must emit no stray
    // box-drawing characters into a structured log sink.
    using var sw = new StringWriter();

    WhizbangBanner.PrintHeader("OrderService", enabled: false, writer: sw);

    await Assert.That(sw.ToString()).IsEmpty();
  }

  [Test]
  public async Task PrintHeader_DrawsAClosedBoxOfUniformWidthAsync() {
    // A box whose rows disagree on width renders as a torn frame in a terminal. The
    // padding arithmetic is the only thing holding the corners together, so pin it.
    WhizbangBanner.OutputRedirectedOverride = true;   // plain banner keeps the parse simple
    using var sw = new StringWriter();

    WhizbangBanner.PrintHeader(
      "OrderService", "1.2.3",
      new Dictionary<string, string> { ["Mode"] = "Ai" },
      whizbangVersion: "9.9.9", writer: sw);

    var box = _boxLines(sw.ToString());
    await Assert.That(box.Count).IsEqualTo(4)
      .Because("top border, title, one config row, bottom border");
    await Assert.That(box.Select(l => l.Length).Distinct().Count()).IsEqualTo(1)
      .Because("every row of the frame has to be the same width or the corners do not meet");
    await Assert.That(box[0]).StartsWith("  ╔");
    await Assert.That(box[0]).EndsWith("╗");
    await Assert.That(box[^1]).StartsWith("  ╚");
    await Assert.That(box[^1]).EndsWith("╝");
  }

  [Test]
  public async Task PrintHeader_TitleCarriesNameAndBothVersionsAsync() {
    // The header is the one place an operator reads which build is running against which
    // library version, so both numbers have to survive into the title row.
    WhizbangBanner.OutputRedirectedOverride = true;
    using var sw = new StringWriter();

    WhizbangBanner.PrintHeader("OrderService", "1.2.3", whizbangVersion: "9.9.9", writer: sw);

    var title = _boxLines(sw.ToString())[1];
    await Assert.That(title).Contains("OrderService");
    await Assert.That(title).Contains("v1.2.3");
    await Assert.That(title).Contains("(Whizbang v9.9.9)");
  }

  [Test]
  public async Task PrintHeader_WithoutAnExplicitVersion_FallsBackToAPlaceholderAsync() {
    // A caller that cannot supply a version still gets a well-formed title rather than
    // "v" followed by nothing.
    WhizbangBanner.OutputRedirectedOverride = true;
    using var sw = new StringWriter();

    WhizbangBanner.PrintHeader("whizbang-migrate", version: null, whizbangVersion: "9.9.9", writer: sw);

    await Assert.That(_boxLines(sw.ToString())[1]).Contains("v0.0.0");
  }

  [Test]
  public async Task PrintHeader_RendersParametersOrderedByKeyAsync() {
    // Parameters arrive in whatever order the caller's dictionary enumerates. The header
    // sorts them so the same run configuration always reads identically -- otherwise two
    // hosts with identical settings produce diffs that look like real changes.
    WhizbangBanner.OutputRedirectedOverride = true;
    using var sw = new StringWriter();

    var unordered = new Dictionary<string, string> {
      ["Zone"] = "west",
      ["Action"] = "Prepare",
      ["Mode"] = "Ai",
    };

    WhizbangBanner.PrintHeader("PR Runner", "1.0.0", unordered, whizbangVersion: "9.9.9", writer: sw);

    var config = _boxLines(sw.ToString())[2];
    await Assert.That(config).Contains("Action: Prepare | Mode: Ai | Zone: west")
      .Because("the rendered order is alphabetical by key, not the dictionary's own order");
  }

  [Test]
  public async Task PrintHeader_WithNoParameters_OmitsTheConfigRowAsync() {
    // An empty configuration must collapse the row rather than draw an empty band.
    WhizbangBanner.OutputRedirectedOverride = true;
    using var sw = new StringWriter();
    using var withParams = new StringWriter();

    WhizbangBanner.PrintHeader("OrderService", "1.0.0", whizbangVersion: "9.9.9", writer: sw);
    WhizbangBanner.PrintHeader(
      "OrderService", "1.0.0",
      new Dictionary<string, string> { ["Mode"] = "Ai" },
      whizbangVersion: "9.9.9", writer: withParams);

    await Assert.That(_boxLines(sw.ToString()).Count).IsEqualTo(3)
      .Because("top border, title, bottom border -- and nothing between");
    await Assert.That(_boxLines(withParams.ToString()).Count).IsEqualTo(4);
  }

  [Test]
  public async Task PrintHeader_PrintsTheBannerAboveTheBoxAsync() {
    // PrintHeader is banner + box; if it silently stopped calling Print the box would
    // still look correct on its own, so assert the two are ordered and both present.
    WhizbangBanner.OutputRedirectedOverride = true;
    using var banner = new StringWriter();
    using var header = new StringWriter();

    WhizbangBanner.Print(banner);
    WhizbangBanner.PrintHeader("OrderService", "1.0.0", whizbangVersion: "9.9.9", writer: header);

    var firstBannerRow = banner.ToString()
      .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
    var text = header.ToString();

    await Assert.That(text).Contains(firstBannerRow);
    await Assert.That(text.IndexOf(firstBannerRow, StringComparison.Ordinal))
      .IsLessThan(text.IndexOf('╔'))
      .Because("the banner is printed before the configuration box, not after it");
  }

  // --- Logging sinks ---------------------------------------------------------

  [Test]
  public async Task LogBanner_LogsThePlainBannerOneRowPerEntryAsync() {
    // Structured sinks strip ANSI and treat each entry as a record, so the plain banner
    // has to arrive a row at a time rather than as one blob with embedded newlines.
    WhizbangBanner.OutputRedirectedOverride = true;
    var logger = new RecordingLogger();
    using var sw = new StringWriter();
    WhizbangBanner.Print(sw);
    var expected = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.TrimEnd('\r')).ToList();

    WhizbangBanner.LogBanner(logger);

    await Assert.That(logger.Entries.Count).IsEqualTo(expected.Count)
      .Because("one log entry per banner row");
    await Assert.That(logger.Entries).IsEquivalentTo(expected);
    await Assert.That(logger.Entries.Any(e => e.Contains('\x1b'))).IsFalse()
      .Because("this overload is the one for sinks that cannot render escape codes");
  }

  [Test]
  public async Task LogBannerAnsi_LogsTheColoredBannerAsASingleEntryAsync() {
    // The ANSI overload targets terminal-aware sinks, where splitting the gradient across
    // entries would break the background run between rows.
    WhizbangBanner.OutputRedirectedOverride = false;
    Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
    var logger = new RecordingLogger();

    WhizbangBanner.LogBannerAnsi(logger);

    await Assert.That(logger.Entries.Count).IsEqualTo(1);
    await Assert.That(logger.Entries[0]).Contains("\x1b[");
  }

  /// <summary>Returns only the configuration box rows, discarding the banner above it.</summary>
  private static List<string> _boxLines(string output)
    => [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.TrimEnd('\r'))
        .Where(l => l.Contains('╔') || l.Contains('║') || l.Contains('╚'))];

  private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger {
    public bool Enabled { get; init; } = true;
    public List<string> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => Enabled;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
      => Entries.Add(formatter(state, exception));
  }
}
