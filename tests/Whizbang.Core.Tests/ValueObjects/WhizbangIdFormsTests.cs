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

  /// <summary>An id written by hand: no generator registers its form, so running its module's initializer finds none.</summary>
  private readonly struct HandWrittenId : IWhizbangId {
    public Guid ToGuid() => Guid.Empty;
    public bool IsTimeOrdered => false;
    public bool SubMillisecondPrecision => false;
    public DateTimeOffset Timestamp => DateTimeOffset.MinValue;
    public bool Equals(IWhizbangId? other) => other is HandWrittenId;
    public int CompareTo(IWhizbangId? other) => 0;
  }

  [Test]
  public async Task UnregisteredType_HasNoFormAsync() {
    await Assert.That(WhizbangIdForms.TryGet(typeof(Guid), out _)).IsFalse();
  }

  [Test]
  public async Task UnregisteredId_HasNoFormAfterItsModuleInitializerRunsAsync() {
    await Assert.That(WhizbangIdForms.TryGet(typeof(HandWrittenId), out var form)).IsFalse();
    await Assert.That(form).IsNull();
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
