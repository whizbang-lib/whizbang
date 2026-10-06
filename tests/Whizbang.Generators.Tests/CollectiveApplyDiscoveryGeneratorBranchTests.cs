// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Naming the defaults explicitly, <c>ScopeHandling = Framework</c> and <c>SpecKind = Linq</c>, emits
/// the same registration as leaving them out; only the non-zero values select Custom and RawSql.
/// </summary>
/// <remarks>
/// Written against the framework's own attribute rather than a stand-in: a stand-in declared inside
/// a <c>Whizbang.Core.*</c> namespace resolves <c>System.Attribute</c> against the framework's
/// <c>Whizbang.Core.System</c> namespace, does not bind, and leaves every named argument an error
/// value, so the generator never reads it.
/// </remarks>
/// <tests>src/Whizbang.Generators/CollectiveApplyDiscoveryGenerator.cs</tests>
[Category("SourceGenerators")]
public class CollectiveApplyDiscoveryGeneratorBranchTests {
  private static string _registry(string arguments) {
    var result = GeneratorTestHelper.RunGenerator<CollectiveApplyDiscoveryGenerator>($$"""
      using Whizbang.Core.Messaging;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public sealed class JobModel { }

      public sealed record TouchEvent(
        CollectiveScope Scope,
        System.Collections.Generic.IReadOnlyList<System.Guid> MatchedStreamIds) : ICollectiveEvent;

      public sealed class JobPerspective {
        [CollectiveApplyFor({{arguments}})]
        public ICollectiveSpec<JobModel> Touch(TouchEvent e) => null!;
      }
      """);
    return GeneratorTestHelper.GetGeneratedSource(result, "CollectiveApplyRegistry.g.cs") ?? "";
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ExplicitDefaults_EmitFrameworkAndLinqAsync() {
    var code = _registry("ScopeHandling = CollectiveScopeHandling.Framework, SpecKind = CollectiveSpecKind.Linq");

    await Assert.That(code).Contains("Touch")
      .Because("the control: the handler is registered");
    await Assert.That(code).Contains("CollectiveScopeHandling.Framework");
    await Assert.That(code).Contains("CollectiveSpecKind.Linq");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task NonZeroValues_EmitCustomAndRawSqlAsync() {
    var code = _registry("ScopeHandling = CollectiveScopeHandling.Custom, SpecKind = CollectiveSpecKind.RawSql");

    await Assert.That(code).Contains("CollectiveScopeHandling.Custom");
    await Assert.That(code).Contains("CollectiveSpecKind.RawSql");
  }
}
