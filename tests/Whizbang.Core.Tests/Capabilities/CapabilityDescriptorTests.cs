// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Capabilities;

/// <summary>
/// The provider-independent vocabulary: a capability's identity, its descriptor and the fix it gives for
/// each state, and which states are usable.
/// </summary>
/// <docs>operations/infrastructure/capabilities</docs>
public class CapabilityDescriptorTests {
  private static CapabilityDescriptor _withRemedy(CapabilityState state, string remedy) =>
    FakeCapabilityProvider.Describe("broker", "delayed_exchange") with {
      Remedies = new Dictionary<CapabilityState, string> { [state] = remedy },
    };

  [Test]
  public async Task Id_ReadsAsProviderSlashNameAsync() {
    await Assert.That(new CapabilityId("postgres", "pg_squeeze").ToString()).IsEqualTo("postgres/pg_squeeze");
  }

  [Test]
  [Arguments(CapabilityState.Ready)]
  [Arguments(CapabilityState.Provisionable)]
  public async Task Remedy_UsableStates_NeedNothingAsync(CapabilityState state) {
    await Assert.That(_withRemedy(state, "ignored").Remedy(state)).IsEqualTo("none");
  }

  [Test]
  public async Task Remedy_UsesTheProvidersFixForTheStateAsync() {
    var descriptor = _withRemedy(CapabilityState.NotPermitted, "enable the plugin in the broker's allowlist");

    await Assert.That(descriptor.Remedy(CapabilityState.NotPermitted)).IsEqualTo("enable the plugin in the broker's allowlist");
  }

  private static CapabilityDescriptor _withDetailRemedy() =>
    _withRemedy(CapabilityState.NeedsServerChange, "enable the plugin, then restart the broker") with {
      DetailRemedies = new Dictionary<string, string>(StringComparer.Ordinal) {
        ["NeedsFeatureFlag"] = "turn the feature flag on; no restart is needed",
      },
    };

  [Test]
  public async Task Remedy_WithADetailThatHasItsOwnFix_UsesThatFixAsync() {
    await Assert.That(_withDetailRemedy().Remedy(CapabilityState.NeedsServerChange, "NeedsFeatureFlag"))
      .IsEqualTo("turn the feature flag on; no restart is needed");
  }

  [Test]
  [Arguments("NeedsPluginEnabled")]
  [Arguments(null)]
  public async Task Remedy_WithADetailWithoutAFixOfItsOwn_UsesTheStatesFixAsync(string? detail) {
    await Assert.That(_withDetailRemedy().Remedy(CapabilityState.NeedsServerChange, detail))
      .IsEqualTo("enable the plugin, then restart the broker");
  }

  [Test]
  public async Task Remedy_AUsableState_NeedsNothing_WhateverTheDetailAsync() {
    await Assert.That(_withDetailRemedy().Remedy(CapabilityState.Ready, "NeedsFeatureFlag")).IsEqualTo("none");
  }

  [Test]
  public async Task DetailRemedies_DefaultToNoneAsync() {
    await Assert.That(FakeCapabilityProvider.Describe("broker", "delayed_exchange").DetailRemedies.Count).IsEqualTo(0);
  }

  [Test]
  [Arguments(CapabilityState.NotPermitted, "permit")]
  [Arguments(CapabilityState.NeedsServerChange, "server")]
  [Arguments(CapabilityState.NoPrivilege, "identity")]
  [Arguments(CapabilityState.Missing, "install")]
  [Arguments(CapabilityState.Unknown, "probe")]
  public async Task Remedy_FallsBackToAGenericFixWhenTheProviderGaveNoneAsync(CapabilityState state, string mentions) {
    var remedy = FakeCapabilityProvider.Describe("broker", "delayed_exchange").Remedy(state);

    await Assert.That(remedy).Contains(mentions);
  }

  [Test]
  [Arguments(CapabilityState.Ready, true)]
  [Arguments(CapabilityState.Provisionable, true)]
  [Arguments(CapabilityState.NotPermitted, false)]
  [Arguments(CapabilityState.NeedsServerChange, false)]
  [Arguments(CapabilityState.NoPrivilege, false)]
  [Arguments(CapabilityState.Missing, false)]
  [Arguments(CapabilityState.Unknown, false)]
  public async Task IsUsable_OnlyForReadyAndProvisionableAsync(CapabilityState state, bool usable) {
    var status = new CapabilityStatus(FakeCapabilityProvider.Describe("broker", "x"), "vhost-a", state, null, null, null);

    await Assert.That(status.IsUsable).IsEqualTo(usable);
  }

  [Test]
  public async Task Unknown_GivesEveryDescriptorTheUnknownStateAndTheReasonAsync() {
    var descriptors = new[] { FakeCapabilityProvider.Describe("broker", "a"), FakeCapabilityProvider.Describe("broker", "b") };

    var statuses = CapabilityStatus.Unknown(descriptors, "vhost-a", "unreachable");

    await Assert.That(statuses.Count).IsEqualTo(2);
    await Assert.That(statuses.All(s => s.State == CapabilityState.Unknown && s.Scope == "vhost-a"
      && s.Reason == "unreachable" && s.Version is null && s.Detail is null)).IsTrue();
  }
}
