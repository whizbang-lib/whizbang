// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Attributes;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for <see cref="FieldPermissionMasking"/>: who may see a <c>[FieldPermission]</c> member, and what
/// everyone else sees under each <see cref="MaskingStrategy"/>.
/// </summary>
/// <tests>FieldPermissionMasking</tests>
public class FieldPermissionMaskingTests {
  private static ImmutableScopeContext _scopeWith(params string[] permissions) =>
    new(new SecurityExtraction {
      Scope = new PerspectiveScope(),
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(permissions.Select(p => new Permission(p))),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test",
    }, shouldPropagate: false);

  // ===== IsPermitted =====

  [Test]
  public async Task IsPermitted_ScopeHoldsThePermission_IsTrueAsync() {
    var permitted = FieldPermissionMasking.IsPermitted(_scopeWith("pii:view"), new FieldPermissionAttribute("pii:view"));

    await Assert.That(permitted).IsTrue();
  }

  [Test]
  public async Task IsPermitted_ScopeLacksThePermission_IsFalseAsync() {
    var permitted = FieldPermissionMasking.IsPermitted(_scopeWith("orders:read"), new FieldPermissionAttribute("pii:view"));

    await Assert.That(permitted).IsFalse();
  }

  [Test]
  public async Task IsPermitted_NoScope_IsFalseAsync() {
    var permitted = FieldPermissionMasking.IsPermitted(null, new FieldPermissionAttribute("pii:view"));

    await Assert.That(permitted).IsFalse().Because("a caller with no scope holds no permissions: the check fails closed");
  }

  [Test]
  public async Task IsPermitted_NullAttribute_ThrowsAsync() {
    await Assert.That(() => FieldPermissionMasking.IsPermitted(_scopeWith(), null!))
      .Throws<ArgumentNullException>();
  }

  // ===== MaskString =====

  [Test]
  [Arguments(MaskingStrategy.Mask, "123-45-6789", "****")]
  [Arguments(MaskingStrategy.Mask, null, "****")]
  [Arguments(MaskingStrategy.Partial, "123-45-6789", "****6789")]
  [Arguments(MaskingStrategy.Partial, "12345", "****2345")]
  [Arguments(MaskingStrategy.Partial, "1234", "****")]
  [Arguments(MaskingStrategy.Partial, "12", "****")]
  [Arguments(MaskingStrategy.Partial, null, "****")]
  [Arguments(MaskingStrategy.Redact, "123-45-6789", "[REDACTED]")]
  [Arguments(MaskingStrategy.Redact, null, "[REDACTED]")]
  public async Task MaskString_ReplacesTheValueWithItsPlaceholderAsync(MaskingStrategy strategy, string? value, string expected) {
    var masked = FieldPermissionMasking.MaskString(value, strategy);

    await Assert.That(masked).IsEqualTo(expected);
  }

  [Test]
  public async Task MaskString_Hide_ReturnsNullAsync() {
    var masked = FieldPermissionMasking.MaskString("123-45-6789", MaskingStrategy.Hide);

    await Assert.That(masked).IsNull();
  }

  [Test]
  public async Task MaskString_UndefinedStrategy_HidesTheValueAsync() {
    var masked = FieldPermissionMasking.MaskString("123-45-6789", (MaskingStrategy)99);

    await Assert.That(masked).IsNull().Because("a strategy the framework does not know withholds the value rather than showing it");
  }

  // ===== MaskValue =====

  [Test]
  public async Task MaskValue_StringMember_UsesTheStringPlaceholderAsync() {
    var masked = FieldPermissionMasking.MaskValue("123-45-6789", MaskingStrategy.Partial, isStringMember: true);

    await Assert.That(masked).IsEqualTo("****6789");
  }

  [Test]
  [Arguments(MaskingStrategy.Hide)]
  [Arguments(MaskingStrategy.Mask)]
  [Arguments(MaskingStrategy.Partial)]
  [Arguments(MaskingStrategy.Redact)]
  public async Task MaskValue_NonStringMember_IsHiddenUnderEveryStrategyAsync(MaskingStrategy strategy) {
    var masked = FieldPermissionMasking.MaskValue(50_000m, strategy, isStringMember: false);

    await Assert.That(masked).IsNull().Because("a placeholder string cannot stand in for a number, so a non-string member is hidden");
  }

  // ===== IsWithheld =====

  [Test]
  [Arguments(MaskingStrategy.Hide, true, true)]
  [Arguments(MaskingStrategy.Mask, true, false)]
  [Arguments(MaskingStrategy.Partial, true, false)]
  [Arguments(MaskingStrategy.Redact, true, false)]
  [Arguments((MaskingStrategy)99, true, true)]
  [Arguments(MaskingStrategy.Hide, false, true)]
  [Arguments(MaskingStrategy.Mask, false, true)]
  [Arguments(MaskingStrategy.Partial, false, true)]
  [Arguments(MaskingStrategy.Redact, false, true)]
  public async Task IsWithheld_IsTrueExactlyWhenNoPlaceholderAppliesAsync(MaskingStrategy strategy, bool isStringMember, bool expected) {
    var withheld = FieldPermissionMasking.IsWithheld(strategy, isStringMember);

    await Assert.That(withheld).IsEqualTo(expected);
  }
}
