// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>A type only these tests register.</summary>
public sealed record DeclaredRequiredMembersProbe(double X, double Y);

/// <summary>A second type only these tests register, to show a later registration replaces an earlier one.</summary>
public sealed record DeclaredRequiredMembersReplaced(double X, double Y);

/// <summary>
/// Issue #1874: the members a type declares required whose generated reader tolerates their absence, because the
/// type is read from history as well as from perspective rows. The startup check reads them to report rows missing a
/// member the code declares required.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Serialization/DeclaredRequiredMembers.cs</code-under-test>
[Category("Serialization")]
public class DeclaredRequiredMembersTests {
  [Test]
  public async Task ARegisteredMember_IsDeclared_ByItsJsonNameAsync() {
    DeclaredRequiredMembers.Register(typeof(DeclaredRequiredMembersProbe), "x", "Y");

    await Assert.That(DeclaredRequiredMembers.IsDeclared(typeof(DeclaredRequiredMembersProbe), "x")).IsTrue();
    await Assert.That(DeclaredRequiredMembers.IsDeclared(typeof(DeclaredRequiredMembersProbe), "Y")).IsTrue();
    await Assert.That(DeclaredRequiredMembers.IsDeclared(typeof(DeclaredRequiredMembersProbe), "X")).IsFalse()
      .Because("the declared name is the key the stored document holds, matched exactly");
  }

  [Test]
  public async Task ATypeNeverRegistered_DeclaresNothingAsync() {
    await Assert.That(DeclaredRequiredMembers.IsDeclared(typeof(DeclaredRequiredMembersTests), "x")).IsFalse();
  }

  [Test]
  public async Task ALaterRegistration_ReplacesTheEarlierOneAsync() {
    DeclaredRequiredMembers.Register(typeof(DeclaredRequiredMembersReplaced), "x");
    DeclaredRequiredMembers.Register(typeof(DeclaredRequiredMembersReplaced), "y");

    await Assert.That(DeclaredRequiredMembers.IsDeclared(typeof(DeclaredRequiredMembersReplaced), "y")).IsTrue();
    await Assert.That(DeclaredRequiredMembers.IsDeclared(typeof(DeclaredRequiredMembersReplaced), "x")).IsFalse()
      .Because("every context that registers a type derives the same names from the same declaration");
  }

  [Test]
  public async Task NullArguments_AreRefusedAsync() {
    await Assert.That(() => DeclaredRequiredMembers.Register(null!, "x")).Throws<ArgumentNullException>();
    await Assert.That(() => DeclaredRequiredMembers.Register(typeof(DeclaredRequiredMembersProbe), null!)).Throws<ArgumentNullException>();
    await Assert.That(() => DeclaredRequiredMembers.IsDeclared(null!, "x")).Throws<ArgumentNullException>();
    await Assert.That(() => DeclaredRequiredMembers.IsDeclared(typeof(DeclaredRequiredMembersProbe), null!)).Throws<ArgumentNullException>();
  }
}
