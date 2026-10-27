// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Configuration;

/// <summary>
/// #1490: .NET binding ignores a key nothing binds, so a deployment that still sets a removed or
/// misspelled <c>Whizbang:*</c> key ran on defaults without a word. The check compares every configured
/// <c>Whizbang:*</c> key with the keys the library reads (the configuration key manifest, embedded in
/// Whizbang.Core) and reports each key nothing reads, naming the replacement of a removed one.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Configuration/UnknownConfigurationKeyCheck.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Configuration/KnownConfigurationKeys.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Configuration/RemovedConfigurationKeys.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Configuration/ConfigurationCheckOptions.cs</code-under-test>
/// <docs>operations/configuration/configuration-reference#unknown-keys</docs>
[Category("Shard2")]
public sealed class UnknownConfigurationKeyCheckTests {
  private const string MANIFEST = """
    # header comment
    Whizbang:Workers:Maintenance:IntervalMinutes

    Whizbang:CircuitBreakers:*:FailureThreshold
    Whizbang:PerspectiveRowRetention:Overrides:*
    """;

  private static KnownConfigurationKeys _known() => KnownConfigurationKeys.Parse(MANIFEST);

  private static IConfiguration _configuration(params (string Key, string? Value)[] settings) =>
    new ConfigurationBuilder()
      .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
      .Build();

  private const string MAINTENANCE_INTERVAL = "Whizbang:WorkCoordinator:MaintenanceInterval";

  // ---- the known-key set ----

  [Test]
  public async Task IsKnown_AManifestedKey_IgnoringCaseAsync() {
    var known = _known();

    await Assert.That(known.IsKnown("Whizbang:Workers:Maintenance:IntervalMinutes")).IsTrue();
    await Assert.That(known.IsKnown("whizbang:workers:maintenance:intervalminutes")).IsTrue()
      .Because("configuration keys are case-insensitive, so a differently cased key still binds");
  }

  [Test]
  public async Task IsKnown_ATypoUnderAKnownSection_IsUnknownAsync() {
    var known = _known();

    await Assert.That(known.IsKnown("Whizbang:Workers:Maintenance:IntervalMinute")).IsFalse();
    await Assert.That(known.IsKnown("Whizbang:Workers:Maintenance")).IsFalse()
      .Because("a value on the section itself binds to nothing");
  }

  [Test]
  public async Task IsKnown_ATrailingWildcard_KnowsEveryEntryBelowItAsync() {
    var known = _known();

    await Assert.That(known.IsKnown("Whizbang:PerspectiveRowRetention:Overrides:OrderView")).IsTrue()
      .Because("a dictionary section's entries are names the consumer chooses");
    await Assert.That(known.IsKnown("Whizbang:PerspectiveRowRetention:Overrides:OrderView:RetentionDays")).IsTrue()
      .Because("a dictionary entry may itself be an object");
    await Assert.That(known.IsKnown("Whizbang:PerspectiveRowRetention:Overrides")).IsFalse()
      .Because("the wildcard stands for at least one name");
  }

  [Test]
  public async Task IsKnown_AnInnerWildcard_MatchesExactlyOneSegmentAsync() {
    var known = _known();

    await Assert.That(known.IsKnown("Whizbang:CircuitBreakers:orders-db:FailureThreshold")).IsTrue();
    await Assert.That(known.IsKnown("Whizbang:CircuitBreakers:orders-db:FailureTreshold")).IsFalse()
      .Because("a misspelled property under a named entry is still a typo");
    await Assert.That(known.IsKnown("Whizbang:CircuitBreakers:a:b:FailureThreshold")).IsFalse();
    await Assert.That(known.IsKnown("Whizbang:CircuitBreakers:orders-db")).IsFalse();
  }

  [Test]
  public async Task Library_IsTheEmbeddedManifestAsync() {
    var library = KnownConfigurationKeys.Library;

    await Assert.That(library.IsKnown("Whizbang:Workers:Maintenance:IntervalMinutes")).IsTrue();
    await Assert.That(library.IsKnown("Whizbang:Configuration:UnknownKeys")).IsTrue()
      .Because("the check's own switch is a key the library reads");
    await Assert.That(library.IsKnown("Whizbang:PerspectiveRowRetention:Overrides:AnyModel")).IsTrue();
    await Assert.That(library.IsKnown(MAINTENANCE_INTERVAL)).IsFalse();
    await Assert.That(ReferenceEquals(library, KnownConfigurationKeys.Library)).IsTrue()
      .Because("the manifest is read once per process");
  }

  [Test]
  public async Task FromResource_AMissingResource_ThrowsNamingItAsync() {
    void act() => KnownConfigurationKeys.FromResource(typeof(KnownConfigurationKeys).Assembly, "no-such-resource.txt");

    var thrown = await Assert.That(act).Throws<InvalidOperationException>();
    await Assert.That(thrown!.Message).Contains("no-such-resource.txt");
  }

  // ---- removed keys ----

  [Test]
  public async Task Removed_MaintenanceInterval_NamesTheMaintenanceKeysAsync() {
    var removed = RemovedConfigurationKeys.Find("whizbang:workcoordinator:maintenanceinterval");

    await Assert.That(removed).IsNotNull();
    await Assert.That(removed!.ReplacedBy).Contains("Whizbang:Workers:Maintenance:IntervalMinutes");
    await Assert.That(removed.ReplacedBy).Contains("Whizbang:Housekeeping:SettledCooldown");
    await Assert.That(removed.ReplacedBy).Contains("Whizbang:Housekeeping:MaxDeferral");
    await Assert.That(removed.ReplacedBy).DoesNotContain("Whizbang:Housekeeping:MaxConsecutiveDeferrals");
    await Assert.That(RemovedConfigurationKeys.Find("Whizbang:Workers:Maintenance:IntervalMinutes")).IsNull();
  }

  [Test]
  [Arguments("AzureServiceBus")]
  [Arguments("RabbitMQ")]
  public async Task Removed_TransportInboxBatchSettings_NameTheInboxBatchKeysAsync(string transport) {
    // #1536: MessageProcessing's three inbox batch settings were never read; inbox batching is configured
    // under Whizbang:Workers:InboxBatch, so a deployment that still sets them is pointed there.
    var section = $"Whizbang:Transports:{transport}:MessageProcessing";
    (string Removed, string Replacement)[] expected = [
      ($"{section}:InboxBatchSize", "Whizbang:Workers:InboxBatch:MaxSize"),
      ($"{section}:InboxBatchSlideMs", "Whizbang:Workers:InboxBatch:SlidingWindow"),
      ($"{section}:InboxBatchMaxWaitMs", "Whizbang:Workers:InboxBatch:MaxWait"),
    ];

    foreach (var (key, replacement) in expected) {
      await Assert.That(KnownConfigurationKeys.Library.IsKnown(key)).IsFalse()
        .Because($"nothing reads {key}, so the startup check must report it");
      var removed = RemovedConfigurationKeys.Find(key);
      await Assert.That(removed).IsNotNull();
      await Assert.That(removed!.ReplacedBy).IsEquivalentTo([replacement]);
    }
  }

  [Test]
  public async Task Removed_EveryReplacementIsAKeyTheLibraryReads_AndNoRemovedKeyIsAsync() {
    await Assert.That(RemovedConfigurationKeys.All).IsNotEmpty();
    foreach (var removed in RemovedConfigurationKeys.All) {
      await Assert.That(KnownConfigurationKeys.Library.IsKnown(removed.Key)).IsFalse()
        .Because($"{removed.Key} is listed as removed, so nothing may read it");
      foreach (var replacement in removed.ReplacedBy) {
        await Assert.That(KnownConfigurationKeys.Library.IsKnown(replacement)).IsTrue()
          .Because($"{removed.Key} points operators at {replacement}, which must be a real key");
      }
    }
  }

  // ---- finding unknown keys ----

  [Test]
  public async Task Find_ReportsOnlyConfiguredWhizbangKeysNothingReadsAsync() {
    var configuration = _configuration(
      ("Whizbang:Workers:Maintenance:IntervalMinutes", "5"),
      ("Whizbang:PerspectiveRowRetention:Overrides:OrderView", "30"),
      ("Whizbang:Workers:Maintenance:IntervalMinute", "5"),
      (MAINTENANCE_INTERVAL, "00:05:00"),
      ("Logging:LogLevel:Default", "Information"),
      ("ServiceName", "orders"));

    var unknown = UnknownConfigurationKeyCheck.Find(configuration, _known());

    await Assert.That(unknown.Select(u => u.Key)).IsEquivalentTo(
      [MAINTENANCE_INTERVAL, "Whizbang:Workers:Maintenance:IntervalMinute"]);
    await Assert.That(unknown.Single(u => u.Key == MAINTENANCE_INTERVAL).Removed).IsNotNull();
    await Assert.That(unknown.Single(u => u.Key != MAINTENANCE_INTERVAL).Removed).IsNull();
  }

  [Test]
  public async Task Find_ASectionWithNoValue_IsNotAKeyAsync() {
    var configuration = _configuration(("Whizbang:Workers:Maintenance:IntervalMinutes", "5"));

    var unknown = UnknownConfigurationKeyCheck.Find(configuration, _known());

    await Assert.That(unknown).IsEmpty()
      .Because("Whizbang, Whizbang:Workers and Whizbang:Workers:Maintenance are only the path to the key");
  }

  // ---- reporting ----

  [Test]
  public async Task Run_Warn_LogsOneWarningPerUnknownKeyAsync() {
    var logger = new FakeLogger();
    var configuration = _configuration(
      (MAINTENANCE_INTERVAL, "00:05:00"),
      ("Whizbang:Workers:Maintenance:IntervalMinute", "5"),
      ("Whizbang:PerspectiveRowRetention:Overrides:OrderView", "30"));

    UnknownConfigurationKeyCheck.Run(configuration, _known(), UnknownConfigurationKeyMode.Warn, logger);

    var records = logger.Collector.GetSnapshot();
    await Assert.That(records).Count().IsEqualTo(2);
    await Assert.That(records.All(r => r.Level == LogLevel.Warning)).IsTrue();
    var removed = records.Single(r => r.Message.Contains(MAINTENANCE_INTERVAL, StringComparison.Ordinal));
    await Assert.That(removed.Message).Contains("removed");
    await Assert.That(removed.Message).Contains("Whizbang:Workers:Maintenance:IntervalMinutes");
    await Assert.That(removed.Message).Contains("Whizbang:Housekeeping:SettledCooldown");
    var typo = records.Single(r => !r.Message.Contains(MAINTENANCE_INTERVAL, StringComparison.Ordinal));
    await Assert.That(typo.Message).Contains("'Whizbang:Workers:Maintenance:IntervalMinute'");
    await Assert.That(typo.Message).Contains("not read");
  }

  [Test]
  public async Task Run_Ignore_ReportsNothingAsync() {
    var logger = new FakeLogger();
    var configuration = _configuration((MAINTENANCE_INTERVAL, "00:05:00"));

    UnknownConfigurationKeyCheck.Run(configuration, _known(), UnknownConfigurationKeyMode.Ignore, logger);

    await Assert.That(logger.Collector.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Run_Throw_FailsNamingEveryUnknownKeyAndTheSwitchAsync() {
    var logger = new FakeLogger();
    var configuration = _configuration(
      (MAINTENANCE_INTERVAL, "00:05:00"),
      ("Whizbang:Workers:Maintenance:IntervalMinute", "5"));

    void act() => UnknownConfigurationKeyCheck.Run(configuration, _known(), UnknownConfigurationKeyMode.Throw, logger);

    var thrown = await Assert.That(act).Throws<InvalidOperationException>();
    await Assert.That(thrown!.Message).Contains(MAINTENANCE_INTERVAL);
    await Assert.That(thrown.Message).Contains("Whizbang:Workers:Maintenance:IntervalMinute");
    await Assert.That(thrown.Message).Contains("Whizbang:Configuration:UnknownKeys");
    await Assert.That(logger.Collector.Count).IsEqualTo(2)
      .Because("the log carries the same lines a Warn start would, replacement included");
  }

  [Test]
  public async Task Run_Throw_WithEveryKeyKnown_StartsAsync() {
    var logger = new FakeLogger();
    var configuration = _configuration(("Whizbang:Workers:Maintenance:IntervalMinutes", "5"));

    UnknownConfigurationKeyCheck.Run(configuration, _known(), UnknownConfigurationKeyMode.Throw, logger);

    await Assert.That(logger.Collector.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Run_WithoutConfiguration_ReportsNothingAsync() {
    var logger = new FakeLogger();

    UnknownConfigurationKeyCheck.Run(null, _known(), UnknownConfigurationKeyMode.Throw, logger);

    await Assert.That(logger.Collector.Count).IsEqualTo(0);
  }

  // ---- the switch ----

  [Test]
  public async Task Options_DefaultToWarnAsync() {
    await Assert.That(new ConfigurationCheckOptions().UnknownKeys).IsEqualTo(UnknownConfigurationKeyMode.Warn);
  }

  [Test]
  public async Task Options_BindFromWhizbangConfigurationThroughAddWhizbangAsync() {
    var services = new ServiceCollection();
    services.AddSingleton(_configuration(("Whizbang:Configuration:UnknownKeys", "Throw")));
    services.AddWhizbang();
    await using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<IOptions<ConfigurationCheckOptions>>().Value;

    await Assert.That(options.UnknownKeys).IsEqualTo(UnknownConfigurationKeyMode.Throw);
  }

  [Test]
  public async Task Options_WithoutConfiguration_KeepTheCodeValueAsync() {
    var options = new ConfigurationCheckOptions { UnknownKeys = UnknownConfigurationKeyMode.Ignore };

    new ConfigureConfigurationCheckOptions(null).Configure(options);

    await Assert.That(options.UnknownKeys).IsEqualTo(UnknownConfigurationKeyMode.Ignore);
  }
}
