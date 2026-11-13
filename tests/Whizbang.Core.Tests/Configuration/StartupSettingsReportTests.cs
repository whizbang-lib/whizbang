// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Configuration;

/// <summary>
/// The startup settings report: one entry for every key in the configuration key manifest, with the value it runs with
/// and where that came from (configured and by which provider, default, or derived with its inputs), credentials
/// redacted by type, grouped one section per settings section.
/// </summary>
public class StartupSettingsReportTests {
  private const string MANIFEST = """
    # comment
    ConnectionStrings:*
    ServiceName
    Whizbang:A:One
    Whizbang:A:Two
    Whizbang:B:*:Three
    Whizbang:C:ConnectionString
    Whizbang:C:ApiToken
    Whizbang:C:Plain
    """;

  private static readonly KnownConfigurationKeys _manifest = KnownConfigurationKeys.Parse(MANIFEST);
  private static readonly KnownConfigurationKeys _sensitive = KnownConfigurationKeys.Parse("Whizbang:C:Plain\n");

  private static IConfigurationRoot _config(params (string Key, string Value)[] values) =>
    new ConfigurationBuilder()
      .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
      .Build();

  private static List<SettingEntry> _entries(IConfigurationRoot? configuration, params IDerivedSetting[] derived) =>
    [.. StartupSettingsReport.Build(configuration, _manifest, _sensitive, derived).SelectMany(s => s.Settings)];

  [Test]
  public async Task EveryKeyInTheConfigurationKeyManifest_IsInTheReportAsync() {
    var report = StartupSettingsReport.Build(_config(), KnownConfigurationKeys.Library, KnownConfigurationKeys.Sensitive, []);
    var reported = report.SelectMany(s => s.Settings).Select(e => e.ManifestKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

    var missing = KnownConfigurationKeys.Library.Keys.Where(key => !reported.Contains(key)).ToList();

    await Assert.That(KnownConfigurationKeys.Library.Keys.Count).IsGreaterThan(100);
    await Assert.That(missing).IsEmpty()
      .Because("every key the binders read must be in the startup settings report: " + string.Join(", ", missing));
  }

  [Test]
  public async Task AConfiguredKey_ShowsItsValueAndTheProviderThatSetItAsync() {
    var entry = _entries(_config(("Whizbang:A:One", "42"))).Single(e => e.Key == "Whizbang:A:One");

    await Assert.That(entry.Source).IsEqualTo(SettingSource.Configured);
    await Assert.That(entry.Value).IsEqualTo("42");
    await Assert.That(entry.Provider).Contains("MemoryConfigurationProvider");
    await Assert.That(entry.ToString()).StartsWith("One=42 (configured: ");
  }

  [Test]
  public async Task TheLastProviderThatHasAKey_IsTheOneNamedAsync() {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection([new KeyValuePair<string, string?>("Whizbang:A:One", "first")])
      .AddCommandLine(["--Whizbang:A:One=second"])
      .Build();

    var entry = _entries(configuration).Single(e => e.Key == "Whizbang:A:One");

    await Assert.That(entry.Value).IsEqualTo("second");
    await Assert.That(entry.Provider).Contains("CommandLine");
  }

  [Test]
  public async Task AnUnsetKey_IsTheDefaultAsync() {
    var entry = _entries(_config()).Single(e => e.Key == "Whizbang:A:Two");

    await Assert.That(entry.Source).IsEqualTo(SettingSource.Default);
    await Assert.That(entry.Value).IsNull();
    await Assert.That(entry.ToString()).IsEqualTo("Two (default)");
  }

  [Test]
  public async Task AnUnsetKeyWithADefault_ShowsTheDefaultValue_MarkedDefault_AndASensitiveOneRedactedAsync() {
    var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
      ["Whizbang:A:Two"] = "8",
      ["Whizbang:C:Plain"] = "Host=db;Password=hunter2",
    };

    var entries = StartupSettingsReport.Build(_config(), _manifest, _sensitive, [], defaults).SelectMany(s => s.Settings).ToList();

    var two = entries.Single(e => e.Key == "Whizbang:A:Two");
    await Assert.That(two.Source).IsEqualTo(SettingSource.Default);
    await Assert.That(two.Value).IsEqualTo("8");
    await Assert.That(two.ToString()).IsEqualTo("Two=8 (default)");
    await Assert.That(entries.Single(e => e.Key == "Whizbang:C:Plain").Value).IsEqualTo("Host=db;" + SensitiveString.REDACTED);
    await Assert.That(entries.Single(e => e.Key == "Whizbang:A:One").ToString()).IsEqualTo("One (default)")
      .Because("a key with no recorded default still says it is the default");
  }

  [Test]
  public async Task TheLibraryDefaults_AreTheCodeDefaultsAsync() {
    await Assert.That(ConfigurationKeyDefaults.Library["Whizbang:Workers:CoalescingOutbox:Partitions"]).IsEqualTo("4");
    await Assert.That(ConfigurationKeyDefaults.Library["Whizbang:BatchBuffer:MaxBufferedMessages"]).IsEqualTo("10000");
    await Assert.That(ConfigurationKeyDefaults.Library["Whizbang:Startup:LogSettings"]).IsEqualTo("true");
  }

  [Test]
  public async Task DefaultsText_IsOneKeyEqualsValuePerLine_ValuesMayHoldEqualsSignsAsync() {
    var parsed = ConfigurationKeyDefaults.Parse("# comment\n\nWhizbang:A:B=x=y\r\nnot a default\n=orphan\nWhizbang:A:C=\n");

    await Assert.That(parsed["Whizbang:A:B"]).IsEqualTo("x=y");
    await Assert.That(parsed["whizbang:a:c"]).IsEqualTo("");
    await Assert.That(parsed.Count).IsEqualTo(2);
    await Assert.That(() => ConfigurationKeyDefaults.FromResource(typeof(ConfigurationKeyDefaults).Assembly, "no-such-resource.txt"))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task WithNoConfiguration_EveryKeyIsTheDefaultAsync() {
    var entries = _entries(null);

    await Assert.That(entries.All(e => e.Source == SettingSource.Default)).IsTrue();
    await Assert.That(entries.Count).IsEqualTo(8);
  }

  [Test]
  public async Task AWildcardKey_IsReportedOncePerConfiguredMatch_OrOnceWhenNothingMatchesAsync() {
    var matched = _entries(_config(("Whizbang:B:orders:Three", "3"), ("Whizbang:B:audit:Three", "4")))
      .Where(e => e.ManifestKey == "Whizbang:B:*:Three").ToList();
    var unmatched = _entries(_config()).Where(e => e.ManifestKey == "Whizbang:B:*:Three").ToList();

    await Assert.That(matched.Select(e => e.Key)).IsEquivalentTo(["Whizbang:B:audit:Three", "Whizbang:B:orders:Three"]);
    await Assert.That(matched.All(e => e.Source == SettingSource.Configured)).IsTrue();
    await Assert.That(unmatched.Single().Key).IsEqualTo("Whizbang:B:*:Three");
    await Assert.That(unmatched.Single().Source).IsEqualTo(SettingSource.Default);
  }

  [Test]
  public async Task ADerivedKey_ShowsTheRuleAndTheInputs_AndWinsOverTheConfiguredValueAsync() {
    var derived = DerivedSetting.FractionOf("Whizbang:A:One", 0.05, "TotalAvailableMemoryBytes", 1000);

    var entry = _entries(_config(("Whizbang:A:One", "7")), derived).Single(e => e.Key == "Whizbang:A:One");

    await Assert.That(entry.Source).IsEqualTo(SettingSource.Derived);
    await Assert.That(entry.Derivation).IsSameReferenceAs(derived);
    await Assert.That(entry.ToString()).IsEqualTo("One=50 (derived: 0.05 x TotalAvailableMemoryBytes; TotalAvailableMemoryBytes=1000)");
  }

  [Test]
  public async Task ADerivedValueNoKeySets_IsReportedUnderItsOwnNameAsync() {
    var floor = DerivedSetting.Of("Whizbang:Workers:ThreadPoolFloor", 12, "ProcessorCount");

    var report = StartupSettingsReport.Build(_config(), _manifest, _sensitive, [floor]);
    var section = report.Single(s => s.Name == "Whizbang:Workers");

    await Assert.That(section.Settings.Single().ToString()).IsEqualTo("ThreadPoolFloor=12 (derived: ProcessorCount)");
  }

  [Test]
  public async Task Credentials_AreRedactedByType_ByConnectionString_AndByNameAsync() {
    var entries = _entries(_config(
      ("Whizbang:C:Plain", "Host=db;Password=hunter2"),
      ("ConnectionStrings:main", "Host=db;Password=hunter2"),
      ("Whizbang:C:ApiToken", "hunter2"),
      ("Whizbang:C:ConnectionString", "Server=sql;Pwd=hunter2")));

    await Assert.That(entries.Count(e => e.Source == SettingSource.Configured)).IsEqualTo(4);
    await Assert.That(entries.Any(e => e.Value?.Contains("hunter2", StringComparison.Ordinal) == true)).IsFalse();
    await Assert.That(entries.Single(e => e.Key == "Whizbang:C:Plain").Value).IsEqualTo("Host=db;" + SensitiveString.REDACTED);
  }

  [Test]
  public async Task Sections_AreTheParentKeys_OrderedByName_WithRootKeysTogetherAsync() {
    var report = StartupSettingsReport.Build(_config(("ServiceName", "orders")), _manifest, _sensitive, []);

    await Assert.That(report.Select(s => s.Name)).IsEquivalentTo(
      ["(root)", "ConnectionStrings", "Whizbang:A", "Whizbang:B:*", "Whizbang:C"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(report[0].Settings.Single().Value).IsEqualTo("orders");
  }

  [Test]
  public async Task TheSensitiveKeyList_HoldsTheSettingsBoundToASensitiveValueAsync() {
    await Assert.That(KnownConfigurationKeys.Sensitive.IsKnown("Whizbang:Archive:AzureBlob:ConnectionString")).IsTrue();
    await Assert.That(KnownConfigurationKeys.Sensitive.IsKnown("Whizbang:Workers:PinnedPool:ConnectionStringName")).IsFalse();
  }

  [Test]
  public async Task Build_RefusesMissingArgumentsAsync() {
    await Assert.That(() => StartupSettingsReport.Build(null, null!, _sensitive, [])).Throws<ArgumentNullException>();
    await Assert.That(() => StartupSettingsReport.Build(null, _manifest, null!, [])).Throws<ArgumentNullException>();
    await Assert.That(() => StartupSettingsReport.Build(null, _manifest, _sensitive, null!)).Throws<ArgumentNullException>();
  }
}
