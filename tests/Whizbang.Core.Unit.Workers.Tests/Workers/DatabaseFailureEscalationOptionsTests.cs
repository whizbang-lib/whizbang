// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Issue #1986: <see cref="DatabaseFailureEscalationOptions"/> sets when a worker's run of transient database failures
/// escalates to one Error. The defaults ride out a failover of a few seconds without an Error and still page within
/// about a minute of a database that stays down; a value that cannot be met as written is refused at startup.
/// </summary>
/// <docs>fundamentals/workers/transient-database-failures#when-the-database-keeps-failing</docs>
[Category("Workers")]
public sealed class DatabaseFailureEscalationOptionsTests {
  private const string FAILURES_KEY = "Whizbang:DatabaseFailureEscalation:ConsecutiveFailures";
  private const string FAILING_FOR_KEY = "Whizbang:DatabaseFailureEscalation:FailingFor";

  private static ServiceProvider _hostWith(Dictionary<string, string?> settings) {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangWorkers();
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task Defaults_AreThreeFailuresOverAMinuteAsync() {
    var options = new DatabaseFailureEscalationOptions();

    await Assert.That(options.ConsecutiveFailures).IsEqualTo(3)
      .Because("#1986: more than one or two failures in a row, so a single lost statement never counts as a run");
    await Assert.That(options.FailingFor).IsEqualTo(TimeSpan.FromMinutes(1))
      .Because("#1986: long enough that a failover of a few seconds logs only Warnings, short enough that a database "
             + "that stays down pages within about a minute");
  }

  [Test]
  public async Task Turnkey_NoSetting_ResolvesTheDefaultsAsync() {
    await using var provider = _hostWith([]);

    var options = provider.GetRequiredService<IOptions<DatabaseFailureEscalationOptions>>().Value;

    await Assert.That(options.ConsecutiveFailures).IsEqualTo(3);
    await Assert.That(options.FailingFor).IsEqualTo(TimeSpan.FromMinutes(1));
  }

  [Test]
  public async Task Turnkey_Settings_BindFromConfigurationAsync() {
    await using var provider = _hostWith(new() { [FAILURES_KEY] = "5", [FAILING_FOR_KEY] = "00:02:30" });

    var options = provider.GetRequiredService<IOptions<DatabaseFailureEscalationOptions>>().Value;

    await Assert.That(options.ConsecutiveFailures).IsEqualTo(5)
      .Because("the documented keys are how an operator moves the threshold, so they must reach the running instance");
    await Assert.That(options.FailingFor).IsEqualTo(TimeSpan.FromSeconds(150));
  }

  /// <summary>
  /// The workers take the plain options instance (an optional interface parameter is what WHIZ501 reports), so the
  /// registration hands the container the bound, validated instance.
  /// </summary>
  [Test]
  public async Task Turnkey_ThePlainInstanceIsTheBoundOneAsync() {
    await using var provider = _hostWith(new() { [FAILURES_KEY] = "4" });

    var plain = provider.GetRequiredService<DatabaseFailureEscalationOptions>();

    await Assert.That(plain).IsSameReferenceAs(provider.GetRequiredService<IOptions<DatabaseFailureEscalationOptions>>().Value);
    await Assert.That(plain.ConsecutiveFailures).IsEqualTo(4);
  }

  [Test]
  public async Task Turnkey_RegisteredTwice_ValidatesOnceAsync() {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddWhizbangWorkers();
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetServices<IValidateOptions<DatabaseFailureEscalationOptions>>()).Count().IsEqualTo(1);
  }

  [Test]
  [Arguments(1, 0)]
  [Arguments(3, 60)]
  [Arguments(int.MaxValue, 86400)]
  public async Task Validator_AcceptsWhatCanBeMetAsync(int consecutiveFailures, int failingForSeconds) {
    var result = new DatabaseFailureEscalationOptionsValidator().Validate(null, new DatabaseFailureEscalationOptions {
      ConsecutiveFailures = consecutiveFailures,
      FailingFor = TimeSpan.FromSeconds(failingForSeconds),
    });

    await Assert.That(result.Succeeded).IsTrue();
  }

  [Test]
  [Arguments(0)]
  [Arguments(-1)]
  public async Task Validator_RefusesFewerThanOneFailure_NamingTheKeyAsync(int consecutiveFailures) {
    var result = new DatabaseFailureEscalationOptionsValidator().Validate(null, new DatabaseFailureEscalationOptions {
      ConsecutiveFailures = consecutiveFailures,
      FailingFor = TimeSpan.FromMinutes(1),
    });

    await Assert.That(result.Failed).IsTrue()
      .Because("a run is at least one failure long; zero would escalate a run that has not happened");
    await Assert.That(result.FailureMessage).Contains(FAILURES_KEY).And.Contains(consecutiveFailures.ToString(System.Globalization.CultureInfo.InvariantCulture));
  }

  [Test]
  public async Task Validator_RefusesANegativeTime_NamingTheKeyAsync() {
    var result = new DatabaseFailureEscalationOptionsValidator().Validate(null, new DatabaseFailureEscalationOptions {
      ConsecutiveFailures = 3,
      FailingFor = TimeSpan.FromSeconds(-1),
    });

    await Assert.That(result.Failed).IsTrue();
    await Assert.That(result.FailureMessage).Contains(FAILING_FOR_KEY);
  }

  [Test]
  public async Task Validator_BothWrong_NamesBothKeysAsync() {
    var result = new DatabaseFailureEscalationOptionsValidator().Validate(null, new DatabaseFailureEscalationOptions {
      ConsecutiveFailures = 0,
      FailingFor = TimeSpan.FromSeconds(-1),
    });

    await Assert.That(result.FailureMessage).Contains(FAILURES_KEY).And.Contains(FAILING_FOR_KEY);
  }

  [Test]
  public async Task Validator_NullOptions_ThrowsAsync() {
    await Assert.That(() => new DatabaseFailureEscalationOptionsValidator().Validate(null, null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Turnkey_UnmeetableSetting_IsRefusedAtStartupAsync() {
    await using var provider = _hostWith(new() { [FAILURES_KEY] = "0" });

    await Assert.That(() => provider.GetRequiredService<IStartupValidator>().Validate())
      .Throws<OptionsValidationException>()
      .Because("#1986: a threshold that cannot be met is refused when the host starts, never clamped to one nobody chose");
  }
}
