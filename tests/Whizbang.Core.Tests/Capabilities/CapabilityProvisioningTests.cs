// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Startup;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Capabilities;

/// <summary>
/// The provisioning runner, as the <c>Capabilities</c> step runs it between the probe and the verdict: a required
/// capability that is Provisionable is created, a Ready one with a newer version available is upgraded, and each is
/// recorded as created, already present or upgraded. The three-level <c>AutoProvision</c> switch (global, per
/// provider, per capability, the most specific set level winning) can turn it off, and a capability the switch or
/// the server holds back is reported, never thrown.
/// </summary>
/// <docs>operations/infrastructure/capabilities#provisioning</docs>
public class CapabilityProvisioningTests {
  private const string SCOPE = "vhost-a";

  private sealed class FixedRequirements(params CapabilityRequirement[] requirements) : ICapabilityRequirementSource {
    public IReadOnlyList<ActiveCapabilityRequirement> Resolve(IConfiguration? configuration) =>
      CapabilityActivationResolver.Resolve(requirements, configuration);
  }

  private sealed class FixedActive(params ActiveCapabilityRequirement[] requirements) : ICapabilityRequirementSource {
    public IReadOnlyList<ActiveCapabilityRequirement> Resolve(IConfiguration? configuration) => requirements;
  }

  private static CapabilityRequirement _requires(string name) =>
    new(new CapabilityId("broker", name), $"Feature {name}", $"W:{name}:Enabled", "true", Default: null);

  private static CapabilityRequirement _requiresByDefault(string name) => _requires(name) with { Default = "true" };

  private static IConfiguration _configuration(params (string Key, string Value)[] values) =>
    new ConfigurationBuilder()
      .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
      .Build();

  private static (string, string) _turnedOn(string name) => ($"W:{name}:Enabled", "true");

  private static FakeCapabilityProvider _broker(params string[] names) =>
    new("broker", [SCOPE], [.. names.Select(name => FakeCapabilityProvider.Describe("broker", name))]);

  private static async Task<(CapabilityRegistry Registry, StartupStepReport Report)> _runAsync(
      FakeCapabilityProvider provider,
      IEnumerable<ICapabilityProvisioner> provisioners,
      IConfiguration? configuration,
      ICapabilityRequirementSource requirements,
      CapabilityOptions? options = null,
      ILogger<CapabilitiesStartupStep>? logger = null) {
    var registry = new CapabilityRegistry([provider], NullLogger<CapabilityRegistry>.Instance);
    var step = new CapabilitiesStartupStep(
      registry, Options.Create(options ?? new CapabilityOptions()), [requirements], provisioners, configuration,
      logger ?? NullLogger<CapabilitiesStartupStep>.Instance);
    var report = await step.ExecuteAsync(CancellationToken.None);
    return (registry, report);
  }

  private static CapabilityVerdictEntry _entry(CapabilityRegistry registry, string name) =>
    registry.Verdict.Entries.Single(e => e.Status.Descriptor.Id.Name == name);

  [Test]
  public async Task ProvisioningOff_AnOperatorCreatedCapabilityIsStillDetected_AndReadsAlreadyPresentAsync() {
    var provider = _broker("x");
    var provisioner = new FakeCapabilityProvisioner("broker");

    var (registry, _) = await _runAsync(provider, [provisioner], _configuration(_turnedOn("x")),
      new FixedRequirements(_requires("x")), new CapabilityOptions { AutoProvision = false });

    var entry = _entry(registry, "x");
    await Assert.That(entry.Outcome).IsEqualTo(CapabilityVerdictOutcome.Proceed)
      .Because("provisioning off stops the library creating anything, never it seeing what an operator created");
    await Assert.That(entry.Status.Provisioning).IsEqualTo(CapabilityProvisioningOutcome.Present);
    await Assert.That(entry.Line).IsEqualTo(
      "capability broker/x on 'vhost-a': Ready, already present 1.0. Required by: Feature x (W:x:Enabled = true, explicit)");
    await Assert.That(provisioner.Calls).IsEmpty();
  }

  [Test]
  public async Task ProvisioningOn_AReadyCapabilityWithNothingNewer_IsAlreadyPresent_WithoutAnActionAsync() {
    var provider = _broker("x");
    var provisioner = new FakeCapabilityProvisioner("broker");

    var (registry, _) = await _runAsync(provider, [provisioner], _configuration(_turnedOn("x")), new FixedRequirements(_requires("x")));

    await Assert.That(_entry(registry, "x").Status.Provisioning).IsEqualTo(CapabilityProvisioningOutcome.Present);
    await Assert.That(provisioner.Calls).IsEmpty();
  }

  [Test]
  public async Task AnExplicitRequirementOnAProvisionableCapability_IsCreated_AndPassesTheVerdictAsync() {
    var provider = _broker("x");
    provider.States["x"] = CapabilityState.Provisionable;
    var provisioner = new FakeCapabilityProvisioner("broker");

    var (registry, _) = await _runAsync(provider, [provisioner], _configuration(_turnedOn("x")), new FixedRequirements(_requires("x")));

    var entry = _entry(registry, "x");
    await Assert.That(provisioner.Calls.Single()).IsEqualTo((new CapabilityId("broker", "x"), SCOPE, CapabilityProvisionAction.Create));
    await Assert.That(entry.Outcome).IsEqualTo(CapabilityVerdictOutcome.Proceed)
      .Because("the verdict counts only Ready as satisfied, and provisioning makes it Ready before the verdict is taken");
    await Assert.That(entry.Line).StartsWith("capability broker/x on 'vhost-a': Ready, created 1.0. Required by:");
    await Assert.That(registry.Verdict.Failures).IsEmpty();
  }

  [Test]
  public async Task AnImplicitRequirementOnAProvisionableCapability_IsCreatedAsWellAsync() {
    var provider = _broker("x");
    provider.States["x"] = CapabilityState.Provisionable;
    var provisioner = new FakeCapabilityProvisioner("broker");

    var (registry, _) = await _runAsync(provider, [provisioner], configuration: null, new FixedRequirements(_requiresByDefault("x")));

    await Assert.That(_entry(registry, "x").Outcome).IsEqualTo(CapabilityVerdictOutcome.Use);
    await Assert.That(provisioner.Calls.Count).IsEqualTo(1);
  }

  [Test]
  public async Task ACapabilityNothingRequires_IsNeverProvisionedAsync() {
    var provider = _broker("x", "y");
    provider.States["y"] = CapabilityState.Provisionable;
    var provisioner = new FakeCapabilityProvisioner("broker");

    var (registry, _) = await _runAsync(provider, [provisioner], _configuration(_turnedOn("x")), new FixedRequirements(_requires("x")));

    await Assert.That(provisioner.Calls).IsEmpty()
      .Because("only what a feature in force requires is provisioned; the rest is only detected");
    var y = registry.Statuses.Single(s => s.Descriptor.Id.Name == "y");
    await Assert.That(y.State).IsEqualTo(CapabilityState.Provisionable);
    await Assert.That(y.Provisioning).IsNull();
  }

  [Test]
  public async Task ARequirementScopedToOneScope_ProvisionsOnlyThereAsync() {
    var provider = new FakeCapabilityProvider("broker", ["vhost-a", "vhost-b"], FakeCapabilityProvider.Describe("broker", "x"));
    provider.States["x"] = CapabilityState.Provisionable;
    var provisioner = new FakeCapabilityProvisioner("broker");
    var scoped = new ActiveCapabilityRequirement(_requires("x"), "W:x:Enabled", "true", CapabilityActivation.Explicit) { Scope = "vhost-b" };

    var (registry, _) = await _runAsync(provider, [provisioner], configuration: null, new FixedActive(scoped));

    await Assert.That(provisioner.Calls.Single().Scope).IsEqualTo("vhost-b");
    await Assert.That(registry.GetState(new CapabilityId("broker", "x"), "vhost-a")).IsEqualTo(CapabilityState.Provisionable);
    await Assert.That(registry.GetState(new CapabilityId("broker", "x"), "vhost-b")).IsEqualTo(CapabilityState.Ready);
  }

  [Test]
  public async Task PerCapabilityFalse_OverridesGlobalTrue_AndTheVerdictNamesTheSettingAsync() {
    var provider = _broker("x", "y");
    provider.States["x"] = CapabilityState.Provisionable;
    provider.States["y"] = CapabilityState.Provisionable;
    var provisioner = new FakeCapabilityProvisioner("broker");

    var (registry, _) = await _runAsync(provider, [provisioner],
      _configuration(_turnedOn("x"), _turnedOn("y"), ("Whizbang:Capabilities:broker:x:AutoProvision", "false")),
      new FixedRequirements(_requires("x"), _requires("y")), new CapabilityOptions { AutoProvision = true });

    await Assert.That(provisioner.Calls.Select(c => c.Id.Name)).IsEquivalentTo(["y"]);
    var x = _entry(registry, "x");
    await Assert.That(x.Outcome).IsEqualTo(CapabilityVerdictOutcome.Fail);
    await Assert.That(x.Line).StartsWith(
      "capability broker/x on 'vhost-a': Provisionable, 1.0: provisioning is turned off (Whizbang:Capabilities:broker:x:AutoProvision = false). Fix:");
  }

  [Test]
  public async Task PerProviderFalse_OverridesGlobalTrue_AndPerCapabilityTrue_OverridesThatAsync() {
    var provider = _broker("x", "y");
    provider.States["x"] = CapabilityState.Provisionable;
    provider.States["y"] = CapabilityState.Provisionable;
    var provisioner = new FakeCapabilityProvisioner("broker");

    var (registry, _) = await _runAsync(provider, [provisioner],
      _configuration(_turnedOn("x"), _turnedOn("y"),
        ("Whizbang:Capabilities:broker:AutoProvision", "false"),
        ("Whizbang:Capabilities:broker:y:AutoProvision", "true")),
      new FixedRequirements(_requires("x"), _requires("y")));

    await Assert.That(provisioner.Calls.Select(c => c.Id.Name)).IsEquivalentTo(["y"]);
    await Assert.That(_entry(registry, "x").Status.Reason)
      .IsEqualTo("provisioning is turned off (Whizbang:Capabilities:broker:AutoProvision = false)");
  }

  [Test]
  public async Task AnUpgrade_IsRecordedWithTheNewVersionAsync() {
    var provider = _broker("x");
    provider.AvailableVersions["x"] = "1.1";
    var provisioner = new FakeCapabilityProvisioner("broker");

    var (registry, _) = await _runAsync(provider, [provisioner], _configuration(_turnedOn("x")), new FixedRequirements(_requires("x")));

    var entry = _entry(registry, "x");
    await Assert.That(provisioner.Calls.Single().Action).IsEqualTo(CapabilityProvisionAction.Upgrade);
    await Assert.That(entry.Status.Provisioning).IsEqualTo(CapabilityProvisioningOutcome.Upgraded);
    await Assert.That(entry.Status.Version).IsEqualTo("1.1");
    await Assert.That(entry.Line).StartsWith("capability broker/x on 'vhost-a': Ready, upgraded 1.1. Required by:");
    await Assert.That(registry.Statuses.Single().Provisioning).IsEqualTo(CapabilityProvisioningOutcome.Upgraded)
      .Because("the registry, and so the gauge and the health entry, hold the status after provisioning");
  }

  [Test]
  public async Task AnUpgradeTheSwitchHoldsBack_LeavesItPresent_NamingTheVersionAndTheSettingAsync() {
    var provider = _broker("x");
    provider.AvailableVersions["x"] = "1.1";
    var provisioner = new FakeCapabilityProvisioner("broker");

    var (registry, _) = await _runAsync(provider, [provisioner], _configuration(_turnedOn("x")),
      new FixedRequirements(_requires("x")), new CapabilityOptions { AutoProvision = false });

    var entry = _entry(registry, "x");
    await Assert.That(provisioner.Calls).IsEmpty();
    await Assert.That(entry.Outcome).IsEqualTo(CapabilityVerdictOutcome.Proceed);
    await Assert.That(entry.Status.Provisioning).IsEqualTo(CapabilityProvisioningOutcome.Present);
    await Assert.That(entry.Status.Reason)
      .IsEqualTo("1.1 is available, not upgraded: provisioning is turned off (Whizbang:Capabilities:AutoProvision = false)");
  }

  [Test]
  public async Task NoPrivilege_OnAnImplicitRequirement_IsReportedNeverThrown_AndNothingIsAttemptedAsync() {
    var provider = _broker("x");
    provider.States["x"] = CapabilityState.NoPrivilege;
    var provisioner = new FakeCapabilityProvisioner("broker");
    var logger = new FakeLogger<CapabilitiesStartupStep>();

    var (registry, report) = await _runAsync(provider, [provisioner], configuration: null,
      new FixedRequirements(_requiresByDefault("x")), logger: logger);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Completed);
    await Assert.That(provisioner.Calls).IsEmpty()
      .Because("the probe already knows this identity cannot create it");
    await Assert.That(_entry(registry, "x").Outcome).IsEqualTo(CapabilityVerdictOutcome.Degrade);
    await Assert.That(logger.Collector.GetSnapshot().Single(r => r.Level == LogLevel.Warning).Message)
      .StartsWith("capability broker/x on 'vhost-a': NoPrivilege, 1.0. Fix: provision broker/x once with an identity allowed to");
  }

  [Test]
  public async Task NoPrivilege_AnsweredByTheProvisioner_OnAnImplicitRequirement_IsReportedNeverThrownAsync() {
    var provider = _broker("x");
    provider.States["x"] = CapabilityState.Provisionable;
    var provisioner = new FakeCapabilityProvisioner("broker") {
      Answer = (status, _) => status with { State = CapabilityState.NoPrivilege, Detail = "42501", Reason = "permission denied to create extension" },
    };

    var (registry, report) = await _runAsync(provider, [provisioner], configuration: null, new FixedRequirements(_requiresByDefault("x")));

    var entry = _entry(registry, "x");
    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Completed);
    await Assert.That(entry.Outcome).IsEqualTo(CapabilityVerdictOutcome.Degrade);
    await Assert.That(entry.Line).StartsWith(
      "capability broker/x on 'vhost-a': NoPrivilege, 1.0; 42501: permission denied to create extension. Fix:");
  }

  [Test]
  public async Task AProvisionerThatThrows_IsReportedWithAWarning_AndTheStatusIsKeptAsync() {
    var provider = _broker("x");
    provider.States["x"] = CapabilityState.Provisionable;
    var provisioner = new FakeCapabilityProvisioner("broker") { Failure = new InvalidOperationException("connection reset") };
    var logger = new FakeLogger<CapabilitiesStartupStep>();

    var (registry, report) = await _runAsync(provider, [provisioner], configuration: null,
      new FixedRequirements(_requiresByDefault("x")), logger: logger);

    var entry = _entry(registry, "x");
    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Completed);
    await Assert.That(entry.Status.State).IsEqualTo(CapabilityState.Provisionable);
    await Assert.That(entry.Status.Reason).IsEqualTo("provisioning failed (InvalidOperationException: connection reset)");
    var failed = logger.Collector.GetSnapshot().Single(r => r.Level == LogLevel.Warning && r.Exception is not null);
    await Assert.That(failed.Message).IsEqualTo("Provisioning broker/x on 'vhost-a' (Create) failed; the capability keeps the state the probe read");
  }

  [Test]
  public async Task AnUpgradeThatThrows_LeavesTheCapabilityReadyAndPresentAsync() {
    var provider = _broker("x");
    provider.AvailableVersions["x"] = "1.1";
    var provisioner = new FakeCapabilityProvisioner("broker") { Failure = new InvalidOperationException("no update path") };

    var (registry, _) = await _runAsync(provider, [provisioner], _configuration(_turnedOn("x")), new FixedRequirements(_requires("x")));

    var entry = _entry(registry, "x");
    await Assert.That(entry.Outcome).IsEqualTo(CapabilityVerdictOutcome.Proceed)
      .Because("a failed upgrade leaves the installed version, which still works");
    await Assert.That(entry.Status.Provisioning).IsEqualTo(CapabilityProvisioningOutcome.Present);
    await Assert.That(entry.Status.Reason)
      .IsEqualTo("1.1 is available, not upgraded: provisioning failed (InvalidOperationException: no update path)");
  }

  [Test]
  public async Task ACancellationTheCallerDidNotAskFor_IsReportedLikeAnyFailureAsync() {
    var provider = _broker("x");
    provider.States["x"] = CapabilityState.Provisionable;
    var provisioner = new FakeCapabilityProvisioner("broker") { Failure = new OperationCanceledException("command timed out") };

    var (registry, report) = await _runAsync(provider, [provisioner], configuration: null, new FixedRequirements(_requiresByDefault("x")));

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Completed);
    await Assert.That(_entry(registry, "x").Status.Reason).IsEqualTo("provisioning failed (OperationCanceledException: command timed out)");
  }

  [Test]
  public async Task Cancellation_DuringProvisioning_PropagatesAsync() {
    var provider = _broker("x");
    provider.States["x"] = CapabilityState.Provisionable;
    using var cancellation = new CancellationTokenSource();
    var provisioner = new FakeCapabilityProvisioner("broker") {
      Answer = (_, _) => {
        cancellation.Cancel();
        throw new OperationCanceledException(cancellation.Token);
      },
    };
    var registry = new CapabilityRegistry([provider], NullLogger<CapabilityRegistry>.Instance);
    var step = new CapabilitiesStartupStep(registry, Options.Create(new CapabilityOptions()),
      [new FixedRequirements(_requiresByDefault("x"))], [provisioner], null, NullLogger<CapabilitiesStartupStep>.Instance);

    await Assert.That(async () => await step.ExecuteAsync(cancellation.Token)).Throws<OperationCanceledException>()
      .Because("shutting down is the one thing that ends provisioning early");
  }

  [Test]
  public async Task ACapabilityTheLibraryNeverProvisions_IsLeftForTheOperatorAsync() {
    var provider = new FakeCapabilityProvider("broker", [SCOPE], FakeCapabilityProvider.Describe("broker", "x", provisionable: false));
    provider.States["x"] = CapabilityState.Provisionable;
    var provisioner = new FakeCapabilityProvisioner("broker");

    var (registry, _) = await _runAsync(provider, [provisioner], configuration: null, new FixedRequirements(_requiresByDefault("x")));

    await Assert.That(provisioner.Calls).IsEmpty();
    await Assert.That(_entry(registry, "x").Status.Reason).IsEqualTo("Whizbang does not provision broker/x");
  }

  [Test]
  public async Task AProviderWithNoProvisioner_IsReportedAsSuchAsync() {
    var provider = _broker("x");
    provider.States["x"] = CapabilityState.Provisionable;
    var elsewhere = new FakeCapabilityProvisioner("postgres");

    var (registry, _) = await _runAsync(provider, [elsewhere], configuration: null, new FixedRequirements(_requiresByDefault("x")));

    await Assert.That(elsewhere.Calls).IsEmpty();
    await Assert.That(_entry(registry, "x").Status.Reason).IsEqualTo("no provisioner is registered for broker");
  }

  [Test]
  public async Task ProbeTurnedOff_ProvisionsNothingAsync() {
    var provider = _broker("x");
    provider.States["x"] = CapabilityState.Provisionable;
    var provisioner = new FakeCapabilityProvisioner("broker");

    await _runAsync(provider, [provisioner], configuration: null, new FixedRequirements(_requiresByDefault("x")),
      new CapabilityOptions { ProbeOnStartup = false });

    await Assert.That(provisioner.Calls).IsEmpty()
      .Because("with nothing probed every capability is Unknown, and nothing is provisioned on a guess");
  }
}

