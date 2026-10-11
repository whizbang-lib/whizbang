// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Functions;

namespace Whizbang.Data.EFCore.Postgres.Tests.Functions;

/// <summary>
/// Coverage for <see cref="WhizbangOptionsExtensionInfo.LogFragment"/> — pure metadata EF Core
/// prints into its "services already built" debug diagnostics; no existing test reads it. No
/// database is used in this file.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Functions/WhizbangDbContextOptionsExtensions.cs</code-under-test>
[Category("Shard1")]
public class WhizbangDbContextOptionsExtensionsCoverageTests {

  // EF Core appends every registered extension's LogFragment to its options debug string (surfaced
  // in the "ManyServiceProvidersCreatedWarning" diagnostic and DbContext startup logs). If this
  // returned an empty or unstable string, an operator diagnosing "why did EF Core build a new
  // internal service provider" would lose the one clue that Whizbang's function translator was
  // part of the options fingerprint.
  [Test]
  public async Task LogFragment_IsTheStableWhizbangFunctionsLabelAsync() {
    var extension = new WhizbangOptionsExtension();

    var logFragment = extension.Info.LogFragment;

    await Assert.That(logFragment).IsEqualTo("WhizbangFunctions ")
      .Because("the log fragment is a fixed diagnostic label EF Core appends to the options "
             + "debug string — it must stay stable so operators can grep for it");
  }

  // With consumer translator plugins registered the label also says how many, which is the clue that
  // two contexts differing only in their plugins built different internal service providers.
  [Test]
  public async Task LogFragment_WithConsumerPlugins_CountsThemAsync() {
    var extension = new WhizbangOptionsExtension()
      .WithConsumerPlugin(typeof(WhizbangDbContextOptionsExtensionsCoverageTests), _ => { })
      .WithConsumerPlugin(typeof(string), _ => { });

    var logFragment = extension.Info.LogFragment;

    await Assert.That(logFragment).IsEqualTo("WhizbangFunctions(+2 translator plugin(s)) ");
  }

  // The translators build Npgsql-specific SQL, so a plugin handed another provider's factory must
  // refuse at construction, naming what it got, rather than emit SQL that provider cannot run.
  [Test]
  public async Task TranslatorPlugin_WithANonNpgsqlFactory_RefusesNamingTheFactoryAsync() {
    var foreignFactory = System.Reflection.DispatchProxy.Create<
      Microsoft.EntityFrameworkCore.Query.ISqlExpressionFactory, UnusedFactoryProxy>();

    var thrown = Assert.Throws<InvalidOperationException>(
      () => _ = new WhizbangMethodCallTranslatorPlugin(foreignFactory));

    await Assert.That(thrown.Message).Contains("requires Npgsql provider");
    await Assert.That(thrown.Message).Contains(foreignFactory.GetType().Name);
  }

  /// <summary>A factory from no provider at all: the plugin must refuse it before calling anything.</summary>
  public class UnusedFactoryProxy : System.Reflection.DispatchProxy {
    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
      throw new NotSupportedException($"{targetMethod?.Name} was called on a factory the plugin should have refused.");
  }
}
