// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Issue #1901: <see cref="DeadLetterLoggingOptions"/> chooses the level the dead-letter record is logged at. Error by
/// default, so a consumer that alerts on Error hears about a message that will never succeed; Warning for a consumer
/// whose alerting should not fire on dead letters. No other level is accepted: the host refuses to start rather than
/// silently logging at a level nobody chose.
/// </summary>
/// <docs>operations/configuration/configuration-reference#dead-letter-logging</docs>
[Category("Workers")]
public sealed class DeadLetterLoggingOptionsTests {
  private const string LEVEL_KEY = "Whizbang:DeadLetterLogging:Level";

  private static ServiceProvider _hostWith(Dictionary<string, string?> settings) {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangWorkers();
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task Default_IsErrorAsync() {
    await Assert.That(new DeadLetterLoggingOptions().Level).IsEqualTo(LogLevel.Error)
      .Because("#1901: a dead-lettered message will never succeed on its own, so by default it is an Error a consumer can alert on");
  }

  [Test]
  public async Task Turnkey_NoSetting_ResolvesErrorAsync() {
    await using var provider = _hostWith([]);

    var options = provider.GetRequiredService<IOptions<DeadLetterLoggingOptions>>().Value;

    await Assert.That(options.Level).IsEqualTo(LogLevel.Error);
  }

  [Test]
  public async Task Turnkey_LevelWarning_BindsFromConfigurationAsync() {
    await using var provider = _hostWith(new() { [LEVEL_KEY] = "Warning" });

    var options = provider.GetRequiredService<IOptions<DeadLetterLoggingOptions>>().Value;

    await Assert.That(options.Level).IsEqualTo(LogLevel.Warning)
      .Because("the documented key is how a consumer keeps the old severity, so it must reach the running instance");
  }

  [Test]
  [Arguments(LogLevel.Warning)]
  [Arguments(LogLevel.Error)]
  public async Task Validator_AcceptsWarningAndErrorAsync(LogLevel level) {
    var result = new DeadLetterLoggingOptionsValidator().Validate(null, new DeadLetterLoggingOptions { Level = level });

    await Assert.That(result.Succeeded).IsTrue();
  }

  [Test]
  [Arguments(LogLevel.Trace)]
  [Arguments(LogLevel.Debug)]
  [Arguments(LogLevel.Information)]
  [Arguments(LogLevel.Critical)]
  [Arguments(LogLevel.None)]
  public async Task Validator_RefusesEveryOtherLevel_NamingTheKeyAndTheChoicesAsync(LogLevel level) {
    var result = new DeadLetterLoggingOptionsValidator().Validate(null, new DeadLetterLoggingOptions { Level = level });

    await Assert.That(result.Failed).IsTrue()
      .Because("#1901: only Warning and Error are dead-letter levels; a lower one would hide dead letters below the "
             + "per-attempt Warnings, None would hide them entirely, and Critical is kept for failures of the host itself");
    await Assert.That(result.FailureMessage).Contains(LEVEL_KEY)
      .And.Contains(level.ToString())
      .And.Contains("Warning")
      .And.Contains("Error");
  }

  [Test]
  public async Task Validator_NullOptions_ThrowsAsync() {
    await Assert.That(() => new DeadLetterLoggingOptionsValidator().Validate(null, null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Turnkey_UnsupportedLevel_IsRefusedAtStartupAsync() {
    await using var provider = _hostWith(new() { [LEVEL_KEY] = "Critical" });

    await Assert.That(() => provider.GetRequiredService<IStartupValidator>().Validate())
      .Throws<OptionsValidationException>()
      .Because("#1901: an unsupported level is refused when the host starts, never clamped to a level nobody chose");
  }

  [Test]
  public async Task LevelFrom_NoOptions_IsErrorAsync() {
    await Assert.That(DeadLetterLog.LevelFrom((DeadLetterLoggingOptions?)null)).IsEqualTo(LogLevel.Error);
    await Assert.That(DeadLetterLog.LevelFrom((IOptions<DeadLetterLoggingOptions>?)null)).IsEqualTo(LogLevel.Error);
  }

  [Test]
  public async Task LevelFrom_PlainOptions_IsTheirLevelAsync() {
    await Assert.That(DeadLetterLog.LevelFrom(new DeadLetterLoggingOptions { Level = LogLevel.Warning })).IsEqualTo(LogLevel.Warning);
  }

  /// <summary>
  /// The dead-letter paths take the plain options instance (an optional interface parameter is what WHIZ501 reports),
  /// so the registration hands the container the bound, validated instance for every type-activated worker.
  /// </summary>
  [Test]
  public async Task Turnkey_LevelWarning_ThePlainInstanceIsTheBoundOneAsync() {
    await using var provider = _hostWith(new() { [LEVEL_KEY] = "Warning" });

    var plain = provider.GetRequiredService<DeadLetterLoggingOptions>();

    await Assert.That(plain).IsSameReferenceAs(provider.GetRequiredService<IOptions<DeadLetterLoggingOptions>>().Value);
    await Assert.That(plain.Level).IsEqualTo(LogLevel.Warning);
  }

  [Test]
  public async Task LevelFrom_Options_IsTheirLevelAsync() {
    await Assert.That(DeadLetterLog.LevelFrom(Options.Create(new DeadLetterLoggingOptions { Level = LogLevel.Warning })))
      .IsEqualTo(LogLevel.Warning);
  }
}
