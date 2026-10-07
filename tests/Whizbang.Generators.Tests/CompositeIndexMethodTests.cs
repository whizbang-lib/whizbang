// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

extern alias shared;

using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using JsonIndexDiscovery = shared::Whizbang.Generators.Shared.Models.JsonIndexDiscovery;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Btree is the default index method and is left unwritten, whether the declaration omits
/// <c>Method</c> or names Btree explicitly: the statement is the one it always was.
/// </summary>
/// <code-under-test>src/Whizbang.Generators.Shared/Models/JsonIndexDiscovery.cs</code-under-test>
[Category("SourceGenerators")]
public class CompositeIndexMethodTests {
  [Test]
  public async Task ExplicitBtree_IsLeftUnwrittenLikeTheDefaultAsync() {
    var compilation = GeneratorTestHelper.CreateCompilation("""
      using Whizbang.Core.Perspectives;

      namespace Probe;

      [PerspectiveIndex("Tenant", "Kind", Method = PerspectiveIndexMethod.Btree)]
      public class ExplicitModel {
        public string Tenant { get; init; } = "";
        public string Kind { get; init; } = "";
      }

      [PerspectiveIndex("Tenant", "Kind", Method = PerspectiveIndexMethod.Hash)]
      public class HashModel {
        public string Tenant { get; init; } = "";
        public string Kind { get; init; } = "";
      }
      """);
    await Assert.That(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty()
      .Because("the control: the declarations bind");

    var explicitBtree = JsonIndexDiscovery.CompositesFrom(compilation.GetTypeByMetadataName("Probe.ExplicitModel"));
    var hash = JsonIndexDiscovery.CompositesFrom(compilation.GetTypeByMetadataName("Probe.HashModel"));

    await Assert.That(explicitBtree.Single().Method).IsNull();
    await Assert.That(hash.Single().Method).IsEqualTo("hash");
  }
}
