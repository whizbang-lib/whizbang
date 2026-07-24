using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Smoke-tests for the AOT-safe IConfigureOptions implementation
/// `ConfigureCommitOrderStamperOptionsFromConfiguration`. Covers each
/// branch of the Configure method so a future refactor that changes the
/// key spelling, the parse format, or the option assignment fails this
/// test before it ships.
/// </summary>
public class ConfigureCommitOrderStamperOptionsFromConfigurationTests {

  private static CommitOrderStamperOptions _bind(Dictionary<string, string?> kv) {
    var config = new ConfigurationBuilder().AddInMemoryCollection(kv).Build();
    var configurator = new ConfigureCommitOrderStamperOptionsFromConfiguration(config);
    var options = new CommitOrderStamperOptions();
    configurator.Configure(options);
    return options;
  }

  [Test]
  public async Task NoSection_LeavesDefaultsAsync() {
    var defaults = new CommitOrderStamperOptions();
    var bound = _bind([]);
    await Assert.That(bound.PollingInterval).IsEqualTo(defaults.PollingInterval);
    await Assert.That(bound.BatchSize).IsEqualTo(defaults.BatchSize);
    await Assert.That(bound.DisableStamper).IsEqualTo(defaults.DisableStamper);
  }

  [Test]
  public async Task PollingInterval_ParsesUnderInvariantCultureAsync() {
    var bound = _bind(new Dictionary<string, string?> {
      ["Whizbang:Database:Stamper:PollingInterval"] = "00:00:01.5",
    });
    await Assert.That(bound.PollingInterval).IsEqualTo(System.TimeSpan.FromSeconds(1.5));
  }

  [Test]
  public async Task LeaderElectionRetry_ParsesUnderInvariantCultureAsync() {
    var bound = _bind(new Dictionary<string, string?> {
      ["Whizbang:Database:Stamper:LeaderElectionRetry"] = "00:00:30",
    });
    await Assert.That(bound.LeaderElectionRetry).IsEqualTo(System.TimeSpan.FromSeconds(30));
  }

  [Test]
  public async Task BatchSize_ParsesIntAsync() {
    var bound = _bind(new Dictionary<string, string?> {
      ["Whizbang:Database:Stamper:BatchSize"] = "2048",
    });
    await Assert.That(bound.BatchSize).IsEqualTo(2048);
  }

  [Test]
  public async Task DisableStamper_ParsesBoolAsync() {
    var bound = _bind(new Dictionary<string, string?> {
      ["Whizbang:Database:Stamper:DisableStamper"] = "true",
    });
    await Assert.That(bound.DisableStamper).IsTrue();
  }

  [Test]
  public async Task AdvisoryLockKey_ParsesLongAsync() {
    var bound = _bind(new Dictionary<string, string?> {
      ["Whizbang:Database:Stamper:AdvisoryLockKey"] = "9223372036854775000",
    });
    await Assert.That(bound.AdvisoryLockKey).IsEqualTo(9223372036854775000L);
  }

  [Test]
  public async Task Constructor_NullConfiguration_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => new ConfigureCommitOrderStamperOptionsFromConfiguration(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Configure_NullOptions_ThrowsArgumentNullExceptionAsync() {
    var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var configurator = new ConfigureCommitOrderStamperOptionsFromConfiguration(config);
    await Assert.That(() => configurator.Configure(null!))
      .Throws<ArgumentNullException>();
  }

  /// <summary>
  /// Each <c>TryParse</c> branch in <c>Configure</c> has a fail path:
  /// when the configured string doesn't parse, the option should stay at
  /// its default. Locks all five parse-failure branches in one test so a
  /// future refactor that swaps a TryParse for a Parse (which would throw)
  /// fails here instead of in production.
  /// </summary>
  [Test]
  public async Task UnparseableValues_LeaveDefaultsUntouchedAsync() {
    var defaults = new CommitOrderStamperOptions();
    var bound = _bind(new Dictionary<string, string?> {
      ["Whizbang:Database:Stamper:PollingInterval"] = "not-a-timespan",
      ["Whizbang:Database:Stamper:LeaderElectionRetry"] = "not-a-timespan",
      ["Whizbang:Database:Stamper:BatchSize"] = "not-a-number",
      ["Whizbang:Database:Stamper:DisableStamper"] = "not-a-bool",
      ["Whizbang:Database:Stamper:AdvisoryLockKey"] = "not-a-long",
    });

    await Assert.That(bound.PollingInterval).IsEqualTo(defaults.PollingInterval);
    await Assert.That(bound.LeaderElectionRetry).IsEqualTo(defaults.LeaderElectionRetry);
    await Assert.That(bound.BatchSize).IsEqualTo(defaults.BatchSize);
    await Assert.That(bound.DisableStamper).IsEqualTo(defaults.DisableStamper);
    await Assert.That(bound.AdvisoryLockKey).IsEqualTo(defaults.AdvisoryLockKey);
  }
}
