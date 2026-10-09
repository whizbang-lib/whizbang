// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres.Schema;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The reconcile's settings, read from <c>Whizbang:Schema:Reconcile</c>: the mode, a drop switch per object
/// kind, the fleet gate and the configured pins. Nothing set means apply, every kind, after the fleet converges.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Schema/ManagedSchemaSettings.cs</code-under-test>
/// <docs>fundamentals/perspectives/managed-schema-objects#settings</docs>
[Category("Shard3")]
public class ManagedSchemaSettingsTests {
  private static IConfiguration _config(params (string Key, string Value)[] values) =>
    new ConfigurationBuilder()
      .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
      .Build();

  [Test]
  public async Task NothingConfigured_AppliesEveryKind_AfterTheFleetConvergesAsync() {
    var settings = ManagedSchemaSettings.Read(_config());

    await Assert.That(settings.Mode).IsEqualTo(ReconcileMode.Apply);
    await Assert.That(settings.KeepKinds).IsEmpty();
    await Assert.That(settings.Pins).IsEmpty();
    await Assert.That(settings.DropAfterFleetConverged).IsTrue();
  }

  [Test]
  public async Task NoConfiguration_IsTheDefaultsAsync() {
    await Assert.That(ManagedSchemaSettings.Read(null).Mode).IsEqualTo(ReconcileMode.Apply);
  }

  [Test]
  [Arguments("Apply", ReconcileMode.Apply)]
  [Arguments("addonly", ReconcileMode.AddOnly)]
  [Arguments("ReportOnly", ReconcileMode.ReportOnly)]
  [Arguments("OFF", ReconcileMode.Off)]
  public async Task TheMode_IsReadIgnoringCaseAsync(string value, ReconcileMode expected) {
    var settings = ManagedSchemaSettings.Read(_config(("Whizbang:Schema:Reconcile:Mode", value)));

    await Assert.That(settings.Mode).IsEqualTo(expected);
  }

  [Test]
  public async Task AModeThatIsNotOne_FailsSayingWhichValuesAreAsync() {
    var config = _config(("Whizbang:Schema:Reconcile:Mode", "DropEverything"));

    await Assert.That(() => ManagedSchemaSettings.Read(config)).Throws<InvalidOperationException>()
      .WithMessageContaining("Apply, AddOnly, ReportOnly, Off");
  }

  [Test]
  public async Task AKindSwitchedOff_IsKeptAndTheRestAreNotAsync() {
    var settings = ManagedSchemaSettings.Read(_config(
      ("Whizbang:Schema:Reconcile:Drop:Index", "false"),
      ("Whizbang:Schema:Reconcile:drop:trigger", "False"),
      ("Whizbang:Schema:Reconcile:Drop:Constraint", "true")));

    await Assert.That(settings.KeepKinds).IsEquivalentTo([ManagedObjectKind.Index, ManagedObjectKind.Trigger]);
  }

  [Test]
  public async Task AKindThatIsNotOne_FailsNamingItAsync() {
    var config = _config(("Whizbang:Schema:Reconcile:Drop:Indexes", "false"));

    await Assert.That(() => ManagedSchemaSettings.Read(config)).Throws<InvalidOperationException>()
      .WithMessageContaining("Drop:Indexes");
  }

  [Test]
  public async Task ADropSwitchThatIsNotABoolean_FailsNamingItAsync() {
    var config = _config(("Whizbang:Schema:Reconcile:Drop:Index", "nope"));

    await Assert.That(() => ManagedSchemaSettings.Read(config)).Throws<InvalidOperationException>()
      .WithMessageContaining("Drop:Index");
  }

  [Test]
  public async Task TheFleetGate_CanBeTurnedOffAsync() {
    var settings = ManagedSchemaSettings.Read(_config(("Whizbang:Schema:Reconcile:DropAfterFleetConverged", "false")));

    await Assert.That(settings.DropAfterFleetConverged).IsFalse();
  }

  [Test]
  public async Task Pins_AreReadInOrderAsync() {
    var settings = ManagedSchemaSettings.Read(_config(
      ("Whizbang:Schema:Reconcile:Pins:0", "wh_per_job:idx_*_legacy"),
      ("Whizbang:Schema:Reconcile:Pins:1", "*:customer_*")));

    await Assert.That(settings.Pins).IsEquivalentTo(["wh_per_job:idx_*_legacy", "*:customer_*"]);
  }

  [Test]
  public async Task ForAPass_CarriesWhatTheFleetDeclaresAsync() {
    var settings = ManagedSchemaSettings.Read(_config(("Whizbang:Schema:Reconcile:Mode", "AddOnly")));
    var fleet = new HashSet<string> { "wh_per_job:idx_job_status" };

    var forPass = settings.ForPass(fleet);

    await Assert.That(forPass.Mode).IsEqualTo(ReconcileMode.AddOnly);
    await Assert.That(forPass.FleetDeclared).IsSameReferenceAs(fleet);
  }

  [Test]
  public async Task ForAPass_AnUnknownFleet_StaysUnknownAsync() {
    await Assert.That(ManagedSchemaSettings.Read(_config()).ForPass(fleetDeclared: null).FleetDeclared).IsNull();
  }

  [Test]
  public async Task ForAPass_WithoutTheFleetGate_IgnoresTheFleetAsync() {
    var settings = ManagedSchemaSettings.Read(_config(("Whizbang:Schema:Reconcile:DropAfterFleetConverged", "false")));

    await Assert.That(settings.ForPass(fleetDeclared: null).FleetDeclared).IsNotNull().And.IsEmpty();
  }
}
