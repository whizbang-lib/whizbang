// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Component.Tests.Configuration;

/// <summary>
/// The startup settings report as a host writes it (#2251): once the service is running, one Information entry per
/// settings section; with <c>Whizbang:Startup:LogSettings</c> off, only a line saying so; and never a credential.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Configuration/StartupSettingsReportService.cs</code-under-test>
public partial class StartupSettingsReportServiceTests {
  private const string SECRET = "hunter2-Zq9";

  [GeneratedRegex(@"(?i)(password|pwd|sharedaccesskey|accountkey|secret|token|apikey)\s*=\s*[^;\s\]]|://[^/\s:@]+:[^/\s@]+@|sig=")]
  private static partial Regex _knownSecretPattern();

  private static ServiceProvider _provider(IReadOnlyDictionary<string, string?> settings, params IDerivedSettingSource[] derived) {
    var services = new ServiceCollection();
    services.AddFakeLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    foreach (var source in derived) {
      services.AddSingleton(source);
    }
    services.AddWhizbangStartupSettingsReport();
    return services.BuildServiceProvider();
  }

  private static StartupSettingsReportService _report(IServiceProvider provider) =>
    provider.GetServices<IHostedService>().OfType<StartupStageGatedService<StartupSettingsReportService>>().Single().Inner;

  private static FakeLogRecord[] _records(IServiceProvider provider) =>
    [.. provider.GetFakeLogCollector().GetSnapshot().Where(r => r.Category == typeof(StartupSettingsReportService).FullName)];

  private sealed class FixedDerived(params IDerivedSetting[] settings) : IDerivedSettingSource {
    public IEnumerable<IDerivedSetting> DerivedSettings => settings;
  }

  [Test]
  public async Task TheReport_IsHostedAtTheRunningStageAsync() {
    await using var provider = _provider(new Dictionary<string, string?>());

    var gated = provider.GetServices<IHostedService>().OfType<StartupStageGatedService<StartupSettingsReportService>>().Single();

    await Assert.That(gated.Stage).IsEqualTo(StartupStage.Running);
  }

  [Test]
  public async Task AddWhizbang_RegistersTheReport_AndARepeatAddsNothingAsync() {
    var services = new ServiceCollection();
    services.AddWhizbang();
    services.AddWhizbangStartupSettingsReport();

    var registrations = services.Count(d => d.ServiceType == typeof(IHostedService)
      && d.ImplementationFactory?.Method.ReturnType == typeof(StartupStageGatedService<StartupSettingsReportService>));

    await Assert.That(registrations).IsEqualTo(1);
  }

  [Test]
  public async Task Starting_WritesOneInformationEntryPerSection_WithEverySettingAsync() {
    await using var provider = _provider(
      new Dictionary<string, string?> { ["Whizbang:Startup:RegistrationCollisions"] = "Warn" },
      new FixedDerived(DerivedSetting.Of("Whizbang:Workers:ThreadPoolFloor", 12, "ProcessorCount", SettingInput.Of("ProcessorCount", 12))));

    await _report(provider).StartAsync(CancellationToken.None);

    var records = _records(provider);
    var sections = records.Select(r => r.StructuredState!.Single(p => p.Key == "Section").Value).ToList();
    await Assert.That(records.All(r => r.Level == LogLevel.Information)).IsTrue();
    await Assert.That(sections.Count).IsEqualTo(sections.Distinct().Count()).Because("one entry per section");
    var startup = records.Single(r => r.Message.StartsWith("Settings Whizbang:Startup:", StringComparison.Ordinal));
    await Assert.That(startup.Message).Contains("LogSettings (default)");
    await Assert.That(startup.Message).Contains("RegistrationCollisions=Warn (configured: MemoryConfigurationProvider");
    await Assert.That(records.Single(r => r.Message.StartsWith("Settings Whizbang:Workers:", StringComparison.Ordinal)).Message)
      .Contains("ThreadPoolFloor=12 (derived: ProcessorCount; ProcessorCount=12)");
  }

  [Test]
  public async Task TurnedOff_WritesOnlyThatTheReportIsOffAsync() {
    await using var provider = _provider(new Dictionary<string, string?> { ["Whizbang:Startup:LogSettings"] = "false" });

    await _report(provider).StartAsync(CancellationToken.None);

    var records = _records(provider);
    await Assert.That(records.Length).IsEqualTo(1);
    await Assert.That(records[0].Message).IsEqualTo("The startup settings report is off (Whizbang:Startup:LogSettings = false)");
  }

  [Test]
  public async Task NoKnownSecretPattern_ReachesTheLogAsync() {
    // Every credential the library reads, set to a value that carries a secret in each common shape.
    var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    foreach (var key in KnownConfigurationKeys.Library.Keys.Where(k => StartupSettingsReport.IsSensitive(k, KnownConfigurationKeys.Sensitive))) {
      settings[key.Replace("*", "main", StringComparison.Ordinal)] = $"Host=db;Database=app;Password={SECRET};SharedAccessKey={SECRET};AccountKey={SECRET}";
    }
    settings["ConnectionStrings:uri"] = $"amqp://user:{SECRET}@broker:5672/";
    settings["ConnectionStrings:blob"] = $"https://acct.blob.core.windows.net/c?sv=1&sig={SECRET}";
    settings["Whizbang:BodyOffload:Cipher:KeyEncryptionKey"] = SECRET;
    await using var provider = _provider(settings);

    await _report(provider).StartAsync(CancellationToken.None);

    var log = string.Join("\n", _records(provider).Select(r => r.Message));
    await Assert.That(settings.Count).IsGreaterThan(6);
    await Assert.That(log).Contains("Database=app;" + SensitiveString.REDACTED).Because("the credentials were configured and reported");
    await Assert.That(log).DoesNotContain(SECRET);
    await Assert.That(_knownSecretPattern().IsMatch(log)).IsFalse().Because(_knownSecretPattern().Match(log).Value);
  }

  [Test]
  public async Task Stopping_DoesNothingAsync() {
    await using var provider = _provider(new Dictionary<string, string?>());

    await _report(provider).StopAsync(CancellationToken.None);

    await Assert.That(_records(provider)).IsEmpty();
  }
}
