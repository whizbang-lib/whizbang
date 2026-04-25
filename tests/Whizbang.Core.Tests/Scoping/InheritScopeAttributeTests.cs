using Whizbang.Core.Lenses;

namespace Whizbang.Core.Tests.Scoping;

/// <summary>
/// Tests for <see cref="InheritScopeAttribute"/> and <see cref="ScopeFields"/>.
/// </summary>
/// <tests>InheritScopeAttribute,ScopeFields</tests>
public class InheritScopeAttributeTests {
  [Test]
  public async Task ScopeFields_None_HasZeroValueAsync() {
    var value = (int)ScopeFields.None;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task ScopeFields_Tenant_IsDistinctFlagAsync() {
    var tenantOnly = ScopeFields.Tenant;
    var none = ScopeFields.None;
    var intersection = ScopeFields.Tenant & ScopeFields.User;
    await Assert.That(tenantOnly).IsNotEqualTo(none);
    await Assert.That(intersection).IsEqualTo(none);
  }

  [Test]
  public async Task ScopeFields_All_ContainsEveryNamedFieldAsync() {
    var all = ScopeFields.All;
    await Assert.That(all.HasFlag(ScopeFields.Tenant)).IsTrue();
    await Assert.That(all.HasFlag(ScopeFields.User)).IsTrue();
    await Assert.That(all.HasFlag(ScopeFields.Customer)).IsTrue();
    await Assert.That(all.HasFlag(ScopeFields.Organization)).IsTrue();
    await Assert.That(all.HasFlag(ScopeFields.AllowedPrincipals)).IsTrue();
    await Assert.That(all.HasFlag(ScopeFields.Extensions)).IsTrue();
  }

  [Test]
  public async Task ScopeFields_FlagsCombineWithBitwiseOrAsync() {
    var combined = ScopeFields.Tenant | ScopeFields.User;
    await Assert.That(combined.HasFlag(ScopeFields.Tenant)).IsTrue();
    await Assert.That(combined.HasFlag(ScopeFields.User)).IsTrue();
    await Assert.That(combined.HasFlag(ScopeFields.Customer)).IsFalse();
  }

  [Test]
  public async Task InheritScope_Defaults_AreTenantOnCreate_AndNoneAlwaysAsync() {
    var attr = new InheritScopeAttribute();
    await Assert.That(attr.OnCreate).IsEqualTo(ScopeFields.Tenant);
    await Assert.That(attr.Always).IsEqualTo(ScopeFields.None);
  }

  [Test]
  public async Task InheritScope_OnCreate_AcceptsCustomFlagsAsync() {
    var attr = new InheritScopeAttribute { OnCreate = ScopeFields.Tenant | ScopeFields.User };
    await Assert.That(attr.OnCreate).IsEqualTo(ScopeFields.Tenant | ScopeFields.User);
    await Assert.That(attr.Always).IsEqualTo(ScopeFields.None);
  }

  [Test]
  public async Task InheritScope_Always_AcceptsCustomFlagsAsync() {
    var attr = new InheritScopeAttribute { Always = ScopeFields.User };
    await Assert.That(attr.Always).IsEqualTo(ScopeFields.User);
    await Assert.That(attr.OnCreate).IsEqualTo(ScopeFields.Tenant);
  }

  [Test]
  public async Task InheritScope_OnCreateAndAlways_AreIndependentAsync() {
    var attr = new InheritScopeAttribute {
      OnCreate = ScopeFields.Tenant | ScopeFields.AllowedPrincipals,
      Always = ScopeFields.User
    };
    await Assert.That(attr.OnCreate).IsEqualTo(ScopeFields.Tenant | ScopeFields.AllowedPrincipals);
    await Assert.That(attr.Always).IsEqualTo(ScopeFields.User);
  }

  [Test]
  public async Task InheritScope_Attribute_TargetsClassesAsync() {
    var usage = (AttributeUsageAttribute?)Attribute.GetCustomAttribute(
      typeof(InheritScopeAttribute), typeof(AttributeUsageAttribute));
    await Assert.That(usage).IsNotNull();
    await Assert.That(usage!.ValidOn).IsEqualTo(AttributeTargets.Class);
    await Assert.That(usage.AllowMultiple).IsFalse();
  }

  [Test]
  public async Task InheritScope_AppliedToClass_IsDiscoverableViaReflectionAsync() {
    var attr = (InheritScopeAttribute?)Attribute.GetCustomAttribute(
      typeof(SampleAnnotated), typeof(InheritScopeAttribute));
    await Assert.That(attr).IsNotNull();
    await Assert.That(attr!.OnCreate).IsEqualTo(ScopeFields.Tenant | ScopeFields.User);
    await Assert.That(attr.Always).IsEqualTo(ScopeFields.User);
  }

  // Intentional non-perspective sample for reflection discovery — analyzer suppression.
#pragma warning disable WHIZ400
  [InheritScope(OnCreate = ScopeFields.Tenant | ScopeFields.User, Always = ScopeFields.User)]
  private sealed class SampleAnnotated {
  }
#pragma warning restore WHIZ400
}
