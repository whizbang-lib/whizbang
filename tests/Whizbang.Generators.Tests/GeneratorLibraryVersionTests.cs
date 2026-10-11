// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using GeneratorLibraryVersion = Whizbang.Data.EFCore.Postgres.Generators.GeneratorLibraryVersion;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The version the EF Core generator stamps into generated code, which the schema ledger and the
/// instance rows both record. It prefers the informational version, falls back to the assembly
/// version and then to <c>unknown</c>, and strips <c>+build</c> metadata so two builds of one
/// release record the same version.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs</code-under-test>
[Category("SourceGenerators")]
public class GeneratorLibraryVersionTests {
  [Test]
  public async Task From_InformationalVersionWithBuildMetadata_StripsTheMetadataAsync() {
    var version = GeneratorLibraryVersion.From("0.9.4-local.62+abc123", new Version(9, 9, 9, 9));

    await Assert.That(version).IsEqualTo("0.9.4-local.62")
      .Because("the informational version wins over the assembly version, without its build metadata");
  }

  [Test]
  public async Task From_InformationalVersionWithoutMetadata_IsUsedAsIsAsync() {
    await Assert.That(GeneratorLibraryVersion.From("1.0.0-rc.1", null)).IsEqualTo("1.0.0-rc.1");
  }

  [Test]
  public async Task From_NoInformationalVersion_FallsBackToTheAssemblyVersionAsync() {
    await Assert.That(GeneratorLibraryVersion.From(null, new Version(1, 2, 3, 4))).IsEqualTo("1.2.3.4");
  }

  [Test]
  public async Task From_NeitherVersion_ReportsUnknownAsync() {
    await Assert.That(GeneratorLibraryVersion.From(null, null)).IsEqualTo("unknown")
      .Because("a version that cannot be read is recorded as such rather than as an empty string");
  }
}
