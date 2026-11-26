// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// #2596: heavy startup work is declared once, with a scope. A declaration without a defined scope cannot be made, so
/// nothing reaches the fleet-wide cap or the once-per-build rule undeclared, and every framework unit states which rule
/// it runs under.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupWork.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/StartupWorkOptions.cs</code-under-test>
[Category("Startup")]
public class StartupWorkCatalogTests {
  [Test]
  public async Task ADeclaration_KeepsItsNameAndScopeAsync() {
    var declaration = new StartupWorkDeclaration("seed-lookups", StartupWorkScope.PerSchemaPerBuild);

    await Assert.That(declaration.Name).IsEqualTo("seed-lookups");
    await Assert.That(declaration.Scope).IsEqualTo(StartupWorkScope.PerSchemaPerBuild);
  }

  [Test]
  [Arguments(0)]
  [Arguments(3)]
  [Arguments(-1)]
  public async Task WorkDeclaredWithoutADefinedScope_IsRefusedAsync(int scope) {
    await Assert.That(() => new StartupWorkDeclaration("seed-lookups", (StartupWorkScope)scope))
      .Throws<ArgumentOutOfRangeException>()
      .Because("the scope decides whether the fleet runs the work once or on every instance; it is never implied");
  }

  [Test]
  [Arguments("")]
  [Arguments("   ")]
  public async Task WorkDeclaredWithoutAName_IsRefusedAsync(string name) {
    await Assert.That(() => new StartupWorkDeclaration(name, StartupWorkScope.PerInstance)).Throws<ArgumentException>();
  }

  [Test]
  public async Task WorkDeclaredWithANullName_IsRefusedAsync() {
    await Assert.That(() => new StartupWorkDeclaration(null!, StartupWorkScope.PerInstance)).Throws<ArgumentException>();
  }

  [Test]
  public async Task TheFrameworkWork_IsDeclared_WithTheScopeEachRunsUnderAsync() {
    await Assert.That(FrameworkStartupWork.Find(FrameworkStartupWork.ANALYZE_NEW_INDEXES)?.Scope)
      .IsEqualTo(StartupWorkScope.PerInstance).Because("only this instance's own pass queues tables for it");
    await Assert.That(FrameworkStartupWork.Find(FrameworkStartupWork.MANAGED_OBJECT_RECONCILE)?.Scope)
      .IsEqualTo(StartupWorkScope.PerInstance)
      .Because("each run records what this instance declares, and the drop gate reads every live instance's record");
    await Assert.That(FrameworkStartupWork.Find(FrameworkStartupWork.PERSPECTIVE_REGISTRY)?.Scope)
      .IsEqualTo(StartupWorkScope.PerSchemaPerBuild);
    await Assert.That(FrameworkStartupWork.Find(FrameworkStartupWork.PERSPECTIVE_STORAGE_SETTINGS)?.Scope)
      .IsEqualTo(StartupWorkScope.PerSchemaPerBuild);
    await Assert.That(FrameworkStartupWork.Declarations.Count).IsEqualTo(4);
  }

  [Test]
  public async Task EveryFrameworkDeclaration_HasAUniqueName_AndADefinedScopeAsync() {
    var names = FrameworkStartupWork.Declarations.Select(d => d.Name).ToList();

    await Assert.That(names.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(names.Count);
    await Assert.That(FrameworkStartupWork.Declarations
      .All(d => d.Scope is StartupWorkScope.PerInstance or StartupWorkScope.PerSchemaPerBuild)).IsTrue();
  }

  [Test]
  public async Task AnUndeclaredName_IsFoundNowhereAsync() {
    await Assert.That(FrameworkStartupWork.Find("nightly-report")).IsNull();
  }

  [Test]
  public async Task TheOptions_DefaultToACap_SoAHostThatConfiguresNothingIsCappedAsync() {
    var options = new StartupWorkOptions();

    await Assert.That(options.MaxConcurrentHeavyStarts).IsEqualTo(2);
    await Assert.That(options.PermitPollInterval).IsEqualTo(TimeSpan.FromSeconds(1));
    await Assert.That(options.PermitWaitLimit).IsEqualTo(TimeSpan.FromMinutes(5));
    options.Validate();
  }

  [Test]
  public async Task TheOptions_RefuseACapBelowOne_AndANonPositiveIntervalOrLimitAsync() {
    await Assert.That(() => new StartupWorkOptions { MaxConcurrentHeavyStarts = 0 }.Validate())
      .Throws<ArgumentOutOfRangeException>().Because("a cap of zero would hold every start for ever");
    await Assert.That(() => new StartupWorkOptions { PermitPollInterval = TimeSpan.Zero }.Validate())
      .Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => new StartupWorkOptions { PermitWaitLimit = TimeSpan.Zero }.Validate())
      .Throws<ArgumentOutOfRangeException>();
  }
}
