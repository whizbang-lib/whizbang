// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

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
  /// A schema with no gate (not yet migrated, or migrated by an earlier release) is held like any other, so a write that
  /// names no schema keeps the object form while it is (#2518).
  /// </summary>
  [Test]
  public async Task ASchemaWithoutAGate_IsHeld_SoAWriteThatNamesNoSchemaKeepsTheObjectFormAsync() {
    var state = new WhizbangIdDocumentFormState();
    state.Open("db", "orders");
    state.Hold("db", "no_gate");

    await Assert.That(state.WritesUuidString).IsFalse();
    await Assert.That(state.IsHeld("db", "no_gate")).IsTrue();
    await Assert.That(state.IsOpen("db", "orders")).IsTrue();
  }

  /// <summary>
  /// The form is decided per schema for a write that names its target: the UUID string into a schema whose gate is open,
  /// the object form into one that is held or was never asked, whatever the process's other schemas say (#2518).
  /// </summary>
  [Test]
  public async Task AWriteToAnOpenSchema_WritesTheUuidString_WhileAnotherIsHeldAsync() {
    var database = "form_" + Guid.CreateVersion7().ToString("N");
    WhizbangIdDocumentForm.Open(database, "orders");
    WhizbangIdDocumentForm.Hold(database, "no_gate");

    bool open;
    bool held;
    bool unasked;
    using (WhizbangIdDocumentForm.WritingTo(database, "orders")) {
      open = WhizbangIdDocumentForm.WritesUuidString;
      using (WhizbangIdDocumentForm.WritingTo(database, "no_gate")) {
        held = WhizbangIdDocumentForm.WritesUuidString;
      }
    }
    using (WhizbangIdDocumentForm.WritingTo(database, "never_asked")) {
      unasked = WhizbangIdDocumentForm.WritesUuidString;
    }

    await Assert.That(open).IsTrue();
    await Assert.That(held).IsFalse();
    await Assert.That(unasked).IsFalse();
    await Assert.That(WhizbangIdDocumentForm.WritesUuidStringTo(database, "orders")).IsTrue();
    await Assert.That(WhizbangIdDocumentForm.WritesUuidStringTo(database, "no_gate")).IsFalse();
    await Assert.That(WhizbangIdDocumentForm.WritesUuidStringTo(database, "never_asked")).IsFalse();
  }

  [Test]
  public async Task AHoldUnderTheNameADatabaseWasKnownByBefore_MovesToTheServersName_AndKeepsHoldingAsync() {
    var state = new WhizbangIdDocumentFormState();
    state.Hold("?pool/orders_alias", "public");
    state.Open("orders", "audit");

    state.Rename("?pool/orders_alias", "orders");

    await Assert.That(state.IsHeld("orders", "public")).IsTrue().Because("the hold meant the schema the server names");
    await Assert.That(state.IsHeld("?pool/orders_alias", "public")).IsFalse();
    await Assert.That(state.WritesUuidString).IsFalse().Because("the moved hold still holds the process");
    state.Open("orders", "public");
    await Assert.That(state.WritesUuidString).IsTrue()
      .Because("the gate opening under the server's name releases the hold made under the other name");
  }

  [Test]
  public async Task Renaming_LeavesASchemaTheServersNameAlreadyOpened_OpenAsync() {
    var state = new WhizbangIdDocumentFormState();
    state.Open("orders", "public");
    state.Hold("?pool/orders_alias", "public");

    state.Rename("?pool/orders_alias", "orders");

    await Assert.That(state.IsOpen("orders", "public")).IsTrue().Because("a gate seen open stays open");
    await Assert.That(state.WritesUuidString).IsTrue();
  }

  [Test]
  public async Task AWriteToSeveralSchemas_WritesTheUuidString_OnlyWhileEveryOneIsOpenAsync() {
    var database = $"several-{TrackedGuid.New().Value}";
    WhizbangIdDocumentForm.Open(database, "orders");
    WhizbangIdDocumentForm.Hold(database, "audit");

    bool oneHeld, bothOpen, none;
    using (WhizbangIdDocumentForm.WritingTo(database, ["orders", "audit"])) {
      oneHeld = WhizbangIdDocumentForm.WritesUuidString;
      WhizbangIdDocumentForm.Open(database, "audit");
      bothOpen = WhizbangIdDocumentForm.WritesUuidString;
    }
    using (WhizbangIdDocumentForm.WritingTo(database, [])) {
      none = WhizbangIdDocumentForm.WritesUuidString;
    }

    await Assert.That(oneHeld).IsFalse().Because("one schema of the write is held, and its rows would get the UUID string");
    await Assert.That(bothOpen).IsTrue();
    await Assert.That(none).IsFalse().Because("a write that names no schema has no gate that allows the UUID string");
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
