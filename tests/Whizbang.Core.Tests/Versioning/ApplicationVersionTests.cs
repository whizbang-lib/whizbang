// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Versioning;

namespace Whizbang.Core.Tests.Versioning;

/// <summary>
/// The application version an instance records beside its library version (#2027). Both versions can be part of a
/// rollout, so both are recorded on every instance row and in the migration ledger. The rules (owner decisions on
/// #2022): the entry assembly's informational version when it reads as SemVer; a host-supplied explicit version or
/// deploy number overrides it; otherwise the version is unknown, and an unknown version never orders against anything.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Versioning/ApplicationVersion.cs</code-under-test>
[Category("Versioning")]
public class ApplicationVersionTests {
  // ── resolution ──────────────────────────────────────────────────────────

  [Test]
  public async Task Resolve_EntryInformationalVersionThatIsSemVer_IsTheVersionAsync() {
    var version = ApplicationVersion.Resolve(explicitVersion: null, deployNumber: null, entryInformationalVersion: "2.4.1-rc.3+abc123");

    await Assert.That(version.Scheme).IsEqualTo(ApplicationVersionScheme.SemVer);
    await Assert.That(version.Recorded).IsEqualTo("2.4.1-rc.3");
  }

  [Test]
  public async Task Resolve_EntryVersionThatIsNotSemVer_IsUnknownAsync() {
    var version = ApplicationVersion.Resolve(null, null, "1.0.0.0");

    await Assert.That(version.Scheme).IsEqualTo(ApplicationVersionScheme.Unknown);
    await Assert.That(version.Recorded).IsNull();
  }

  [Test]
  public async Task Resolve_NoEntryVersion_IsUnknownAsync() {
    var version = ApplicationVersion.Resolve(null, null, null);

    await Assert.That(version).IsEqualTo(ApplicationVersion.Unknown);
  }

  [Test]
  public async Task Resolve_AnExplicitVersion_OverridesTheEntryAssemblyAsync() {
    var version = ApplicationVersion.Resolve("3.0.0", null, "2.4.1");

    await Assert.That(version.Recorded).IsEqualTo("3.0.0");
    await Assert.That(version.Scheme).IsEqualTo(ApplicationVersionScheme.SemVer);
  }

  [Test]
  public async Task Resolve_ADeployNumber_OverridesTheEntryAssemblyAsync() {
    var version = ApplicationVersion.Resolve(null, 42, "2.4.1");

    await Assert.That(version.Scheme).IsEqualTo(ApplicationVersionScheme.DeployNumber);
    await Assert.That(version.Recorded).IsEqualTo("deploy:42");
  }

  [Test]
  public async Task Resolve_AnExplicitVersionThatIsNotSemVer_FailsLoudlyAsync() {
    // The host said so explicitly: a typo must fail the start, never silently read as unknown.
    await Assert.That(() => ApplicationVersion.Resolve("release-7", null, null)).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task Resolve_BothAnExplicitVersionAndADeployNumber_FailsLoudlyAsync() {
    await Assert.That(() => ApplicationVersion.Resolve("3.0.0", 42, null)).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task Resolve_ANegativeDeployNumber_FailsLoudlyAsync() {
    await Assert.That(() => ApplicationVersion.Resolve(null, -1, null)).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task Resolve_ABlankExplicitVersion_IsNotAnOverrideAsync() {
    var version = ApplicationVersion.Resolve("  ", null, "2.4.1");

    await Assert.That(version.Recorded).IsEqualTo("2.4.1");
  }

  // ── configuration ───────────────────────────────────────────────────────

  [Test]
  public async Task FromConfiguration_ReadsTheExplicitVersionAsync() {
    var configuration = _configuration(("Whizbang:Application:Version", "5.1.0"));

    var version = ApplicationVersion.FromConfiguration(configuration, entryInformationalVersion: "1.0.0");

    await Assert.That(version.Recorded).IsEqualTo("5.1.0");
  }

  [Test]
  public async Task FromConfiguration_ReadsTheDeployNumberAsync() {
    var configuration = _configuration(("Whizbang:Application:DeployNumber", "1234"));

    var version = ApplicationVersion.FromConfiguration(configuration, entryInformationalVersion: "1.0.0");

    await Assert.That(version.Recorded).IsEqualTo("deploy:1234");
  }

  [Test]
  public async Task FromConfiguration_ADeployNumberThatIsNotANumber_FailsLoudlyAsync() {
    var configuration = _configuration(("Whizbang:Application:DeployNumber", "twelve"));

    await Assert.That(() => ApplicationVersion.FromConfiguration(configuration, null)).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task FromConfiguration_NothingConfigured_UsesTheEntryAssemblyAsync() {
    var version = ApplicationVersion.FromConfiguration(_configuration(), entryInformationalVersion: "0.9.0");

    await Assert.That(version.Recorded).IsEqualTo("0.9.0");
  }

  [Test]
  public async Task FromConfiguration_NoConfiguration_UsesTheEntryAssemblyAsync() {
    var version = ApplicationVersion.FromConfiguration(null, entryInformationalVersion: "0.9.0");

    await Assert.That(version.Recorded).IsEqualTo("0.9.0");
  }

  // ── reading a recorded version back ─────────────────────────────────────

  [Test]
  [Arguments("2.4.1", ApplicationVersionScheme.SemVer)]
  [Arguments("deploy:7", ApplicationVersionScheme.DeployNumber)]
  [Arguments("deploy:x", ApplicationVersionScheme.Unknown)]
  [Arguments("not a version", ApplicationVersionScheme.Unknown)]
  [Arguments(null, ApplicationVersionScheme.Unknown)]
  public async Task FromRecorded_ReadsTheSchemeBackAsync(string? recorded, ApplicationVersionScheme scheme) {
    await Assert.That(ApplicationVersion.FromRecorded(recorded).Scheme).IsEqualTo(scheme);
  }

  [Test]
  public async Task FromRecorded_RoundTripsWhatWasRecordedAsync() {
    var original = ApplicationVersion.Resolve(null, 9, null);

    await Assert.That(ApplicationVersion.FromRecorded(original.Recorded)).IsEqualTo(original);
  }

  // ── ordering ────────────────────────────────────────────────────────────

  [Test]
  public async Task CompareTo_TwoSemVers_OrderByPrecedenceAsync() {
    var older = ApplicationVersion.FromRecorded("1.0.0-alpha.2");
    var newer = ApplicationVersion.FromRecorded("1.0.0-alpha.10");

    await Assert.That(newer.CompareTo(older)).IsEqualTo(1);
    await Assert.That(older.CompareTo(newer)).IsEqualTo(-1);
    await Assert.That(older.CompareTo(older)).IsEqualTo(0);
  }

  [Test]
  public async Task CompareTo_TwoDeployNumbers_OrderNumericallyAsync() {
    var older = ApplicationVersion.FromRecorded("deploy:9");
    var newer = ApplicationVersion.FromRecorded("deploy:10");

    await Assert.That(newer.CompareTo(older)).IsEqualTo(1);
    await Assert.That(older.CompareTo(newer)).IsEqualTo(-1);
  }

  [Test]
  public async Task CompareTo_AnUnknownVersion_NeverOrdersAsync() {
    // An unknown app version never stands another instance down, and is never stood down by app version alone.
    var known = ApplicationVersion.FromRecorded("2.0.0");

    await Assert.That(ApplicationVersion.Unknown.CompareTo(known)).IsNull();
    await Assert.That(known.CompareTo(ApplicationVersion.Unknown)).IsNull();
    await Assert.That(ApplicationVersion.Unknown.CompareTo(ApplicationVersion.Unknown)).IsNull();
  }

  [Test]
  public async Task CompareTo_DifferentSchemes_NeverOrderAsync() {
    await Assert.That(ApplicationVersion.FromRecorded("2.0.0").CompareTo(ApplicationVersion.FromRecorded("deploy:3"))).IsNull();
  }

  [Test]
  public async Task ToString_IsWhatIsRecorded_OrUnknownAsync() {
    await Assert.That(ApplicationVersion.FromRecorded("deploy:3").ToString()).IsEqualTo("deploy:3");
    await Assert.That(ApplicationVersion.Unknown.ToString()).IsEqualTo("unknown");
  }

  // ── the provider ────────────────────────────────────────────────────────

  [Test]
  public async Task Provider_ReturnsItsVersionAsync() {
    var version = ApplicationVersion.FromRecorded("deploy:3");

    await Assert.That(new ApplicationVersionProvider(version).ApplicationVersion).IsEqualTo(version);
  }

  private static IConfiguration _configuration(params (string Key, string Value)[] values) =>
    new ConfigurationBuilder()
      .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
      .Build();
}
