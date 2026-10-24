// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Capabilities;

/// <summary>
/// The verdict: one line per required capability and scope, naming every feature that requires it and the
/// setting that turned each one on, with one outcome per row of the validation table. An explicit requirement
/// on a capability that is not Ready fails start; Unknown warns, because the probe could not decide; an implicit
/// requirement degrades.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Capabilities/CapabilityVerdict.cs</code-under-test>
/// <docs>operations/infrastructure/capabilities#verdict</docs>
public class CapabilityVerdictTests {
  private static readonly CapabilityDescriptor _squeeze = FakeCapabilityProvider.Describe("postgres", "pg_squeeze") with {
    Remedies = new Dictionary<CapabilityState, string> {
      [CapabilityState.NotPermitted] = "add PG_SQUEEZE to azure.extensions",
    },
  };

  private static readonly CapabilityRequirement _rewrite =
    new(_squeeze.Id, "Online table rewrite", "Whizbang:Workers:Maintenance:TableRewriteStrategy", "PgSqueeze", "None");

  private static readonly CapabilityRequirement _advice =
    new(new CapabilityId("postgres", "pg_squeeze"), "Bloat advice", "Whizbang:Advisor:Enabled", "true", "true");

  private static ActiveCapabilityRequirement _explicit(CapabilityRequirement requirement) =>
    new(requirement, requirement.Key, requirement.Value, CapabilityActivation.Explicit);

  private static ActiveCapabilityRequirement _implicit(CapabilityRequirement requirement) =>
    new(requirement, requirement.Key, requirement.Default, CapabilityActivation.Implicit);

  private static CapabilityStatus _status(CapabilityState state, string? version = null, string? detail = null, string? reason = null) =>
    new(_squeeze, "orders", state, version, detail, reason);

  private static CapabilityVerdictEntry _only(CapabilityStatus status, params ActiveCapabilityRequirement[] active) =>
    CapabilityVerdict.Evaluate([status], active).Entries.Single();

  [Test]
  public async Task Explicit_Ready_ProceedsAsync() {
    var entry = _only(_status(CapabilityState.Ready, "1.7.0"), _explicit(_rewrite));

    await Assert.That(entry.Outcome).IsEqualTo(CapabilityVerdictOutcome.Proceed);
    await Assert.That(entry.Line).IsEqualTo(
      "capability postgres/pg_squeeze on 'orders': Ready, 1.7.0. Required by: Online table rewrite "
      + "(Whizbang:Workers:Maintenance:TableRewriteStrategy = PgSqueeze, explicit)");
  }

  [Test]
  public async Task Explicit_KnownAndNotReady_FailsStartWithTheFixAsync() {
    var entry = _only(_status(CapabilityState.NotPermitted, detail: "NotAllowlisted"), _explicit(_rewrite));

    await Assert.That(entry.Outcome).IsEqualTo(CapabilityVerdictOutcome.Fail);
    await Assert.That(entry.Line).IsEqualTo(
      "capability postgres/pg_squeeze on 'orders': NotPermitted; NotAllowlisted. Fix: add PG_SQUEEZE to azure.extensions. "
      + "Required by: Online table rewrite (Whizbang:Workers:Maintenance:TableRewriteStrategy = PgSqueeze, explicit)");
  }

  [Test]
  public async Task Explicit_Unknown_WarnsAsync() {
    var entry = _only(_status(CapabilityState.Unknown, reason: "not probed yet"), _explicit(_rewrite));

    await Assert.That(entry.Outcome).IsEqualTo(CapabilityVerdictOutcome.Warn)
      .Because("the probe could not decide; the feature fails loudly when it runs if the capability is missing");
    await Assert.That(entry.Line).IsEqualTo(
      "capability postgres/pg_squeeze on 'orders': Unknown: not probed yet. "
      + "Fix: the probe could not decide; check that postgres is reachable from this service. "
      + "Required by: Online table rewrite (Whizbang:Workers:Maintenance:TableRewriteStrategy = PgSqueeze, explicit)");
  }

  [Test]
  public async Task Implicit_Ready_UsesItAsync() {
    var entry = _only(_status(CapabilityState.Ready, "1.7.0"), _implicit(_advice));

    await Assert.That(entry.Outcome).IsEqualTo(CapabilityVerdictOutcome.Use);
    await Assert.That(entry.Line).IsEqualTo(
      "capability postgres/pg_squeeze on 'orders': Ready, 1.7.0. Required by: Bloat advice (Whizbang:Advisor:Enabled = true, default)");
  }

  [Test]
  public async Task Implicit_NotReady_DegradesAsync() {
    var entry = _only(_status(CapabilityState.NotPermitted, detail: "NotAllowlisted"), _implicit(_advice));

    await Assert.That(entry.Outcome).IsEqualTo(CapabilityVerdictOutcome.Degrade);
    await Assert.That(entry.Line).IsEqualTo(
      "capability postgres/pg_squeeze on 'orders': NotPermitted; NotAllowlisted. Fix: add PG_SQUEEZE to azure.extensions. "
      + "Required by: Bloat advice (Whizbang:Advisor:Enabled = true, default)");
  }

  [Test]
  public async Task Explicit_Provisionable_FailsBecauseNothingHasProvisionedItAsync() {
    var entry = _only(_status(CapabilityState.Provisionable, detail: "Available"), _explicit(_rewrite));

    await Assert.That(entry.Outcome).IsEqualTo(CapabilityVerdictOutcome.Fail)
      .Because("available is not installed: the feature cannot use it until something provisions it");
    await Assert.That(entry.Line).Contains(
      "Provisionable; Available. Fix: postgres/pg_squeeze is available but not installed or enabled; install or enable it. Required by:");
  }

  [Test]
  public async Task OneCapability_RequiredExplicitlyAndImplicitly_IsJudgedExplicitlyAndNamesBothAsync() {
    var entry = _only(_status(CapabilityState.Missing), _implicit(_advice), _explicit(_rewrite));

    await Assert.That(entry.Outcome).IsEqualTo(CapabilityVerdictOutcome.Fail);
    await Assert.That(entry.Line).EndsWith(
      "Required by: Bloat advice (Whizbang:Advisor:Enabled = true, default); "
      + "Online table rewrite (Whizbang:Workers:Maintenance:TableRewriteStrategy = PgSqueeze, explicit)");
  }

  [Test]
  public async Task RequirementNoProviderDescribes_IsUnknownWithTheReasonAsync() {
    var orphan = new CapabilityRequirement(new CapabilityId("broker", "delayed_exchange"), "Delayed delivery", "W:Delay", null, null);

    var entry = CapabilityVerdict.Evaluate([_status(CapabilityState.Ready)], [_explicit(orphan)]).Entries.Single();

    await Assert.That(entry.Outcome).IsEqualTo(CapabilityVerdictOutcome.Warn);
    await Assert.That(entry.Line).IsEqualTo(
      "capability broker/delayed_exchange: Unknown: no registered provider describes it. "
      + "Fix: the probe could not decide; check that broker is reachable from this service. "
      + "Required by: Delayed delivery (W:Delay, explicit)");
  }

  [Test]
  public async Task EachScope_IsItsOwnLine_AndCapabilitiesMatchWithoutRegardToCaseAsync() {
    var requirement = _rewrite with { Capability = new CapabilityId("Postgres", "PG_SQUEEZE") };
    var other = FakeCapabilityProvider.Describe("postgres", "vector");

    var verdict = CapabilityVerdict.Evaluate(
      [_status(CapabilityState.Ready), _status(CapabilityState.Missing) with { Scope = "billing" }, new(other, "orders", CapabilityState.Ready, null, null, null)],
      [_explicit(requirement)]);

    await Assert.That(verdict.Entries.Select(e => e.Status.Scope)).IsEquivalentTo(["orders", "billing"]);
    await Assert.That(verdict.Failures.Single().Status.Scope).IsEqualTo("billing");
  }

  [Test]
  public async Task ScopedRequirement_IsJudgedOnlyAgainstItsOwnScopeAsync() {
    var orders = _status(CapabilityState.Ready);
    var plain = _status(CapabilityState.Provisionable) with { Scope = "plain" };
    var inOrders = _explicit(_rewrite) with { Scope = "orders" };
    var inPlain = _explicit(_rewrite) with { Scope = "plain" };

    var ordersOnly = CapabilityVerdict.Evaluate([orders, plain], [inOrders]);
    var plainOnly = CapabilityVerdict.Evaluate([orders, plain], [inPlain]);

    await Assert.That(ordersOnly.Entries.Select(e => e.Status.Scope)).IsEquivalentTo(["orders"])
      .Because("a database that does not carry the column is not asked for the extension");
    await Assert.That(ordersOnly.Failures).IsEmpty();
    await Assert.That(plainOnly.Failures.Single().Status.Scope).IsEqualTo("plain");
    await Assert.That(ordersOnly.IsRequired(plain)).IsFalse();
  }

  [Test]
  public async Task ScopedRequirement_InAScopeNoProviderProbes_IsUnknownThereAsync() {
    var entry = CapabilityVerdict.Evaluate([_status(CapabilityState.Ready)], [_explicit(_rewrite) with { Scope = "archive" }]).Entries.Single();

    await Assert.That(entry.Outcome).IsEqualTo(CapabilityVerdictOutcome.Warn);
    await Assert.That(entry.Line).StartsWith("capability postgres/pg_squeeze on 'archive': Unknown: no registered provider probes this scope. ");
  }

  [Test]
  public async Task RequiredTag_IsHowEachProbedCapabilityIsRequiredAsync() {
    var ready = _status(CapabilityState.Ready);
    var notRequired = new CapabilityStatus(FakeCapabilityProvider.Describe("postgres", "vector"), "orders", CapabilityState.Ready, null, null, null);
    var explicitly = CapabilityVerdict.Evaluate([ready, notRequired], [_explicit(_rewrite)]);
    var implicitly = CapabilityVerdict.Evaluate([ready], [_implicit(_advice)]);

    await Assert.That(explicitly.RequiredTag(ready)).IsEqualTo("explicit");
    await Assert.That(explicitly.RequiredTag(notRequired)).IsEqualTo("none");
    await Assert.That(explicitly.IsRequired(notRequired)).IsFalse();
    await Assert.That(implicitly.RequiredTag(ready)).IsEqualTo("implicit");
    await Assert.That(implicitly.IsRequired(ready)).IsTrue();
    await Assert.That(CapabilityVerdict.Empty.Entries).IsEmpty();
  }

  [Test]
  public async Task Arguments_AreValidatedAsync() {
    await Assert.That(() => CapabilityVerdict.Evaluate(null!, [])).Throws<ArgumentNullException>();
    await Assert.That(() => CapabilityVerdict.Evaluate([], null!)).Throws<ArgumentNullException>();
  }
}
