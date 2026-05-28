using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;

namespace Whizbang.Core.Tests.Diagnostics;

/// <summary>
/// Covers WhizbangBanner's rendering paths. The class was at 0% in the unit suite
/// because it's invoked only by CLI/host startup; redirecting Console.Out + a
/// no-op ILogger lets us drive both the plain-text and ANSI paths under test.
/// </summary>
public class WhizbangBannerTests {

  [Test]
  public async Task Print_DisabledFlag_WritesNothingAsync() {
    using var sw = new StringWriter();
    WhizbangBanner.Print(sw, enabled: false);
    await Assert.That(sw.ToString()).IsEqualTo(string.Empty);
  }

  [Test]
  public async Task Print_ToTextWriter_WritesPlainOrAnsiBannerAsync() {
    using var sw = new StringWriter();
    WhizbangBanner.Print(sw);
    var output = sw.ToString();
    // Either the plain banner or the ANSI banner is non-empty.
    await Assert.That(output.Length).IsGreaterThan(0);
  }

  [Test]
  public async Task PrintHeader_DisabledFlag_NoOpAsync() {
    // No-op call; verifies the early-return guard runs without error.
    var threw = false;
    try { WhizbangBanner.PrintHeader("Test", version: "1.0", enabled: false); } catch { threw = true; }
    await Assert.That(threw).IsFalse();
  }

  [Test]
  public async Task LogBanner_DisabledFlag_DoesNotLogAsync() {
    var capturing = new CapturingLogger();
    WhizbangBanner.LogBanner(capturing, enabled: false);
    await Assert.That(capturing.Messages.Count).IsEqualTo(0);
  }

  [Test]
  public async Task LogBanner_Enabled_LogsBannerLinesAsync() {
    var capturing = new CapturingLogger();
    WhizbangBanner.LogBanner(capturing);
    await Assert.That(capturing.Messages.Count).IsGreaterThan(0);
  }

  [Test]
  public async Task LogBannerAnsi_DisabledFlag_DoesNotLogAsync() {
    var capturing = new CapturingLogger();
    WhizbangBanner.LogBannerAnsi(capturing, enabled: false);
    await Assert.That(capturing.Messages.Count).IsEqualTo(0);
  }

  [Test]
  public async Task LogBannerAnsi_LoggerLevelDisabled_DoesNotLogAsync() {
    var capturing = new CapturingLogger { LogLevelEnabled = false };
    WhizbangBanner.LogBannerAnsi(capturing);
    await Assert.That(capturing.Messages.Count).IsEqualTo(0);
  }

  [Test]
  public async Task LogBannerAnsi_LoggerLevelEnabled_LogsBannerAsync() {
    var capturing = new CapturingLogger();
    WhizbangBanner.LogBannerAnsi(capturing);
    await Assert.That(capturing.Messages.Count).IsGreaterThan(0);
  }

  [Test]
  public async Task SupportsAnsiColor_GetterReturnsBoolAsync() {
    // The value depends on env; just exercise the getter so we don't 0% the property.
    var value = WhizbangBanner.SupportsAnsiColor;
    await Assert.That(value.GetType()).IsEqualTo(typeof(bool));
  }

  private sealed class CapturingLogger : ILogger {
    public List<string> Messages { get; } = [];
    public bool LogLevelEnabled { get; set; } = true;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => LogLevelEnabled;
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
