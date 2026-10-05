// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Unit tests for <see cref="PerspectiveMemberDefaultRegistry"/>: the model-type and property to declared-default
/// map the perspective-runner generator populates at module load, and which the collective predicate compiler
/// reads so an absent document key is filtered as the value a rebuild would see (#1044).
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>Whizbang.Core/Perspectives/PerspectiveMemberDefaultRegistry.cs</tests>
public class PerspectiveMemberDefaultRegistryTests {
  private sealed class DefaultedModel;
  private sealed class ClearedModel;
  private sealed class UnregisteredModel;
  private sealed class ReRegisteredModel;
  private sealed class ValueTypeModel;

  [Test]
  public async Task Register_ADeclaredDefault_ResolvesItAsync() {
    PerspectiveMemberDefaultRegistry.Register(typeof(DefaultedModel), "Status", "Draft");

    var found = PerspectiveMemberDefaultRegistry.TryResolve(typeof(DefaultedModel), "Status", out var declared);

    await Assert.That(found).IsTrue();
    await Assert.That(declared).IsEqualTo("Draft");
  }

  [Test]
  public async Task Register_AValueTypeDefault_KeepsItsTypeAsync() {
    PerspectiveMemberDefaultRegistry.Register(typeof(ValueTypeModel), "Ordinal", 0L);

    PerspectiveMemberDefaultRegistry.TryResolve(typeof(ValueTypeModel), "Ordinal", out var declared);

    await Assert.That(declared).IsEqualTo(0L)
      .Because("The value is bound as a parameter against the column, so it has to arrive as the CLR type the "
        + "member declares rather than as its text.");
  }

  [Test]
  public async Task Register_Null_RemovesAnEarlierRegistrationAsync() {
    PerspectiveMemberDefaultRegistry.Register(typeof(ClearedModel), "Note", "something");

    PerspectiveMemberDefaultRegistry.Register(typeof(ClearedModel), "Note", null);

    var found = PerspectiveMemberDefaultRegistry.TryResolve(typeof(ClearedModel), "Note", out var declared);
    await Assert.That(found).IsFalse()
      .Because("A member that reads as null needs no help — coalescing it would change a predicate that is already "
        + "correct — so a null default means 'not registered', not 'registered as null'.");
    await Assert.That(declared).IsNull();
  }

  [Test]
  public async Task Register_Twice_KeepsTheLastAsync() {
    PerspectiveMemberDefaultRegistry.Register(typeof(ReRegisteredModel), "Status", "First");
    PerspectiveMemberDefaultRegistry.Register(typeof(ReRegisteredModel), "Status", "Second");

    PerspectiveMemberDefaultRegistry.TryResolve(typeof(ReRegisteredModel), "Status", out var declared);

    await Assert.That(declared).IsEqualTo("Second")
      .Because("Two perspectives over one model each register its members, so re-registration has to be harmless.");
  }

  [Test]
  public async Task TryResolve_AnUnregisteredMember_ReportsNothingAsync() {
    var found = PerspectiveMemberDefaultRegistry.TryResolve(typeof(UnregisteredModel), "Missing", out var declared);

    await Assert.That(found).IsFalse();
    await Assert.That(declared).IsNull();
  }

  [Test]
  public async Task Register_WithoutAModelType_ThrowsAsync() =>
    await Assert.That(() => PerspectiveMemberDefaultRegistry.Register(null!, "Status", "Draft"))
      .Throws<ArgumentNullException>();

  [Test]
  public async Task Register_WithoutAPropertyName_ThrowsAsync() =>
    await Assert.That(() => PerspectiveMemberDefaultRegistry.Register(typeof(DefaultedModel), "", "Draft"))
      .Throws<ArgumentException>();

  [Test]
  public async Task TryResolve_WithoutAModelType_ThrowsAsync() =>
    await Assert.That(() => PerspectiveMemberDefaultRegistry.TryResolve(null!, "Status", out _))
      .Throws<ArgumentNullException>();

  [Test]
  public async Task TryResolve_WithoutAPropertyName_ThrowsAsync() =>
    await Assert.That(() => PerspectiveMemberDefaultRegistry.TryResolve(typeof(DefaultedModel), "", out _))
      .Throws<ArgumentException>();
}
