// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The form a process writes a strongly-typed id in inside a perspective document (#2518): the object form every release
/// reads until every database schema the process writes to has opened its gate, then the UUID string, never back.
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#stored-form</docs>
/// <code-under-test>src/Whizbang.Core/Perspectives/WhizbangIdDocumentForm.cs</code-under-test>
public class WhizbangIdDocumentFormTests {
  [Test]
  public async Task AProcessThatKnowsNoSchema_WritesTheObjectFormAsync() {
    var state = new WhizbangIdDocumentFormState();

    await Assert.That(state.WritesUuidString).IsFalse()
      .Because("nothing says the fleet reads the UUID string, so the form every release reads is written");
  }

  [Test]
  public async Task AHeldSchema_WritesTheObjectForm_UntilItsGateOpensAsync() {
    var state = new WhizbangIdDocumentFormState();

    state.Hold("orders", "public");
    var held = state.WritesUuidString;
    state.Open("orders", "public");

    await Assert.That(held).IsFalse();
    await Assert.That(state.WritesUuidString).IsTrue();
  }

  [Test]
  public async Task OneSchemaStillHeld_HoldsTheWholeProcessAsync() {
    var state = new WhizbangIdDocumentFormState();
    state.Open("orders", "public");
    state.Hold("orders", "audit");

    var oneHeld = state.WritesUuidString;
    state.Open("orders", "audit");

    await Assert.That(oneHeld).IsFalse().Because("the process's writers are shared by every schema it writes to");
    await Assert.That(state.WritesUuidString).IsTrue();
  }

  [Test]
  public async Task TheSameSchemaInAnotherDatabase_IsAnotherSchemaAsync() {
    var state = new WhizbangIdDocumentFormState();
    state.Open("orders", "public");
    state.Hold("billing", "public");

    await Assert.That(state.WritesUuidString).IsFalse();
  }

  [Test]
  public async Task AnOpenSchema_StaysOpen_WhenHeldAgainAsync() {
    var state = new WhizbangIdDocumentFormState();
    state.Open("orders", "public");

    state.Hold("orders", "public");

    await Assert.That(state.WritesUuidString).IsTrue().Because("the gate's latch is one-way");
  }

  [Test]
  public async Task AnEmptySchema_IsPublicAsync() {
    var state = new WhizbangIdDocumentFormState();
    state.Hold("orders", null);

    state.Open("orders", "public");

    await Assert.That(state.WritesUuidString).IsTrue();
  }

  /// <summary>
  /// A schema with no gate is forgotten rather than held, so it does not hold the schemas that have one for the life of
  /// the process (#2518).
  /// </summary>
  [Test]
  public async Task AForgottenSchema_NoLongerHoldsTheProcess_ButAnOpenOneStaysOpenAsync() {
    var state = new WhizbangIdDocumentFormState();
    state.Open("db", "orders");
    state.Hold("db", "no_gate");
    var held = state.WritesUuidString;

    state.Forget("db", "no_gate");
    state.Forget("db", "orders");

    await Assert.That(held).IsFalse();
    await Assert.That(state.WritesUuidString).IsTrue();
    await Assert.That(state.IsHeld("db", "no_gate")).IsFalse();
    await Assert.That(state.IsOpen("db", "orders")).IsTrue().Because("an open schema is never forgotten");
  }

  [Test]
  public async Task ForgettingEveryHeldSchema_LeavesNothingKnown_SoTheObjectFormAsync() {
    var state = new WhizbangIdDocumentFormState();
    state.Hold("db", "no_gate");

    state.Forget("db", "no_gate");
    state.Forget("db", "never_seen");

    await Assert.That(state.WritesUuidString).IsFalse();
    await Assert.That(state.IsHeld("db", "no_gate")).IsFalse();
  }

  [Test]
  public async Task AScope_SetsTheFormForItsFlow_AndRestoresTheOneBeforeAsync() {
    var processForm = WhizbangIdDocumentForm.ProcessWritesUuidString;

    bool inner;
    bool outer;
    using (WhizbangIdDocumentForm.ScopeTo(true)) {
      using (WhizbangIdDocumentForm.ScopeTo(false)) {
        inner = WhizbangIdDocumentForm.WritesUuidString;
      }
      outer = WhizbangIdDocumentForm.WritesUuidString;
    }

    await Assert.That(inner).IsFalse();
    await Assert.That(outer).IsTrue();
    await Assert.That(WhizbangIdDocumentForm.WritesUuidString).IsEqualTo(processForm);
  }
}
