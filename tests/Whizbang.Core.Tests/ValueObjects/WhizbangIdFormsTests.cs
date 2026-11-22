// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// Every generated id registers its stored form (#2518): the unchecked load a storage integration builds its converter
/// from, visited with the id's type in hand.
/// </summary>
/// <code-under-test>src/Whizbang.Core/WhizbangIdForms.cs</code-under-test>
[Category("ValueObjects")]
public class WhizbangIdFormsTests {
  private sealed class IdTypeName : IWhizbangIdFormVisitor<string> {
    public string Visit<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(WhizbangIdForm.ID_MEMBERS)] TId>(WhizbangIdForm<TId> form)
      where TId : struct, IWhizbangId => typeof(TId).Name;
  }

  [Test]
  public async Task GeneratedId_RegistersItsFormAsync() {
    var registered = WhizbangIdForms.TryGet(typeof(StrictVersionTestId), out var form);

    await Assert.That(registered).IsTrue();
    await Assert.That(form!.IdType).IsEqualTo(typeof(StrictVersionTestId));
    await Assert.That(form.Accept(new IdTypeName())).IsEqualTo(nameof(StrictVersionTestId));
  }

  [Test]
  public async Task UnregisteredType_HasNoFormAsync() {
    await Assert.That(WhizbangIdForms.TryGet(typeof(Guid), out _)).IsFalse();
  }

  /// <summary>The load is the unchecked constructor: a stored v4 or empty Guid loads on a strict id.</summary>
  [Test]
  public async Task Load_IsUncheckedAndStoreReadsTheGuidAsync() {
    WhizbangIdForms.TryGet(typeof(StrictVersionTestId), out var untyped);
    var form = (WhizbangIdForm<StrictVersionTestId>)untyped!;
    var v4 = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

    await Assert.That(form.Load(v4).Value).IsEqualTo(v4);
    await Assert.That(form.Load(Guid.Empty).Value).IsEqualTo(Guid.Empty);
    await Assert.That(form.Store(form.Load(v4))).IsEqualTo(v4);
  }
}
