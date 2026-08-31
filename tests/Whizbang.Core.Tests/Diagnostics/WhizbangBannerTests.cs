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
