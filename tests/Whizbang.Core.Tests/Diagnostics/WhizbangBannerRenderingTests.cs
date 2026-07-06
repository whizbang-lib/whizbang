using System.IO;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;

namespace Whizbang.Core.Tests.Diagnostics;

/// <summary>
/// Content/structure tests for WhizbangBanner rendering, complementing
/// WhizbangBannerTests (which only pins the enabled/disabled guards and non-empty
/// output). Print's mode split is driven by <see cref="WhizbangBanner.SupportsAnsiColor"/>,
/// whose first gate is Console.IsOutputRedirected — a process-wide fact tests cannot
/// change — so mode-specific assertions branch on the live value and assert the
/// documented contract of whichever path is active:
///   - plain: fixed-width rows, no ANSI escapes, footer link present;
///   - ANSI: true-color background/foreground codes and per-line reset markers.
/// PrintHeader writes the config box straight to Console.Out, so those tests redirect
/// the console and are [NotInParallel] to avoid cross-test interference.
/// </summary>
/// <docs>operations/observability/diagnostics</docs>
public class WhizbangBannerRenderingTests {
  private const string ESC = "\x1b";
  private const string BACKGROUND_CODE = $"{ESC}[48;2;45;55;72m";
  private const string RESET_CODE = $"{ESC}[0m";
  private const string FOOTER_LINK = "https://whizba.ng/";
  private const int BANNER_WIDTH = 84;
  private const int HEADER_INNER_WIDTH = BANNER_WIDTH - 4;

  // ========================================
  // Print — banner body
  // ========================================

  [Test]
  public async Task Print_RendersContractOfActiveColorModeAsync() {
    await using var sw = new StringWriter();

    WhizbangBanner.Print(sw);
    var output = sw.ToString();

    if (WhizbangBanner.SupportsAnsiColor) {
      // True-color path: dark navy background, 24-bit foreground codes, and the
      // end-of-line marker (background + two spaces + reset) on every banner row.
      await Assert.That(output).Contains(BACKGROUND_CODE);
      await Assert.That(output).Contains("[38;2;");
      await Assert.That(output).Contains(RESET_CODE);
      await Assert.That(output).Contains(BACKGROUND_CODE + "  " + RESET_CODE);
    } else {
      // Plain fallback: no escape codes at all, footer link intact.
      await Assert.That(output.Contains(ESC, StringComparison.Ordinal)).IsFalse();
      await Assert.That(output).Contains(FOOTER_LINK);
    }
  }

  [Test]
  public async Task Print_EmitsNineContentRowsInEitherModeAsync() {
    await using var sw = new StringWriter();

    WhizbangBanner.Print(sw);
    var lines = sw.ToString().Split(Environment.NewLine);
    var contentRows = lines.Where(line => line.Length > 0).ToArray();

    // The generated logo is 9 rows in both the ANSI and plain paths; the ANSI path
    // additionally pads with a leading and trailing blank line, which the
    // non-empty filter removes.
    await Assert.That(contentRows.Length).IsEqualTo(9);
    if (!WhizbangBanner.SupportsAnsiColor) {
      var allFixedWidth = contentRows.All(row => row.Length == BANNER_WIDTH);
      await Assert.That(allFixedWidth).IsTrue();
    }
  }

  [Test]
  public async Task SupportsAnsiColor_WhenOutputRedirected_ReportsFalseAsync() {
    var supports = WhizbangBanner.SupportsAnsiColor;

    // Documented first gate: redirected output can never claim ANSI support.
    var honorsRedirectGate = !Console.IsOutputRedirected || !supports;

    await Assert.That(honorsRedirectGate).IsTrue();
  }

  // ========================================
  // PrintHeader — config box (writes to Console.Out)
  // ========================================

  [Test]
  [NotInParallel]
  public async Task PrintHeader_WithParameters_RendersConfigBoxSortedByKeyAsync() {
    var parameters = new Dictionary<string, string> {
      ["Mode"] = "Fast",
      ["Action"] = "Build"
    };

    var output = _capturePrintHeader(
      w => WhizbangBanner.PrintHeader("TestTool", "1.2.3", parameters, "9.9.9", writer: w));

    var topBorder = "  ╔" + new string('═', HEADER_INNER_WIDTH) + "╗";
    var bottomBorder = "  ╚" + new string('═', HEADER_INNER_WIDTH) + "╝";
    await Assert.That(output).Contains(topBorder);
    await Assert.That(output).Contains(bottomBorder);
    await Assert.That(output).Contains("║  TestTool v1.2.3 (Whizbang v9.9.9)");
    // Keys render alphabetically regardless of insertion order.
    await Assert.That(output).Contains("Action: Build | Mode: Fast");
  }

  [Test]
  [NotInParallel]
  public async Task PrintHeader_NoParametersAndNullVersion_OmitsConfigRowAndDefaultsVersionAsync() {
    var output = _capturePrintHeader(
      w => WhizbangBanner.PrintHeader("MyTool", null, null, "7.7.7", writer: w));

    await Assert.That(output).Contains("MyTool v0.0.0 (Whizbang v7.7.7)");
    var boxRowCount = output.Split(Environment.NewLine).Count(line => line.Contains('║'));
    await Assert.That(boxRowCount).IsEqualTo(1);
  }

  // ========================================
  // LogBanner / LogBannerAnsi — content
  // ========================================

  [Test]
  public async Task LogBanner_Enabled_LogsFixedWidthPlainRowsIncludingFooterLinkAsync() {
    var logger = new CapturingLogger();

    WhizbangBanner.LogBanner(logger);

    await Assert.That(logger.Messages.Count).IsEqualTo(9);
    var allFixedWidth = logger.Messages.All(message => message.Length == BANNER_WIDTH);
    await Assert.That(allFixedWidth).IsTrue();
    var hasFooterLink = logger.Messages.Any(message => message.Contains(FOOTER_LINK, StringComparison.Ordinal));
    await Assert.That(hasFooterLink).IsTrue();
    var hasEscapeCodes = logger.Messages.Any(message => message.Contains(ESC, StringComparison.Ordinal));
    await Assert.That(hasEscapeCodes).IsFalse();
  }

  [Test]
  public async Task LogBannerAnsi_Enabled_LogsSingleEntryWithActiveModeContentAsync() {
    var logger = new CapturingLogger();

    WhizbangBanner.LogBannerAnsi(logger);

    await Assert.That(logger.Messages.Count).IsEqualTo(1);
    var entry = logger.Messages[0];
    if (WhizbangBanner.SupportsAnsiColor) {
      await Assert.That(entry).Contains(RESET_CODE);
    } else {
      await Assert.That(entry).Contains(FOOTER_LINK);
      await Assert.That(entry.Contains(ESC, StringComparison.Ordinal)).IsFalse();
    }
  }

  // ========================================
  // Helpers
  // ========================================

  private static string _capturePrintHeader(Action<TextWriter> print) {
    using var sw = new StringWriter();
    print(sw);
    return sw.ToString();
  }

  private sealed class CapturingLogger : ILogger {
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) {
      Messages.Add(formatter(state, exception));
    }
  }
}
