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
}
