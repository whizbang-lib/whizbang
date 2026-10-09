// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

extern alias shared;

using System.Collections.Immutable;

using Microsoft.CodeAnalysis;

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using KeepSchemaObjectDiscovery = shared::Whizbang.Generators.Shared.Models.KeepSchemaObjectDiscovery;
using KeptSchemaObjectInfo = shared::Whizbang.Generators.Shared.Models.KeptSchemaObjectInfo;

namespace Whizbang.Generators.Tests;

/// <summary>What <c>[KeepSchemaObject]</c> discovery reads from a model: each object it pins, with its reason.</summary>
/// <code-under-test>src/Whizbang.Generators.Shared/Models/KeepSchemaObjectDiscovery.cs</code-under-test>
[Category("SourceGenerators")]
public class KeepSchemaObjectDiscoveryTests {
  private static ImmutableArray<KeptSchemaObjectInfo> _discover(string declarations) {
    var compilation = GeneratorTestHelper.CreateCompilation($$"""
      using Whizbang.Core.Perspectives;

      namespace Probe;

      {{declarations}}
      public class ProbeModel {
        public string Code { get; init; } = "";
      }
      """);
    var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
    if (errors.Length > 0) {
      throw new InvalidOperationException(
        $"the probe source did not compile: {string.Join("; ", errors.Select(e => e.ToString()))}");
    }
    return KeepSchemaObjectDiscovery.From(compilation.GetTypeByMetadataName("Probe.ProbeModel"));
  }

  [Test]
  public async Task EachPinnedObject_IsReadWithItsReasonAsync() {
    var found = _discover("""
      [KeepSchemaObject("idx_probe_legacy", Reason = "the reporting job reads it")]
      [KeepSchemaObject("idx_probe_other")]
      """);

    await Assert.That(found).IsEquivalentTo([
      new KeptSchemaObjectInfo("idx_probe_legacy", "the reporting job reads it"),
      new KeptSchemaObjectInfo("idx_probe_other", null),
    ]);
  }

  [Test]
  public async Task ABlankName_PinsNothingAsync() {
    await Assert.That(_discover("""[KeepSchemaObject(" ")]""")).IsEmpty();
  }

  [Test]
  public async Task AModelWithoutTheAttribute_PinsNothingAsync() {
    await Assert.That(_discover(string.Empty)).IsEmpty();
  }

  [Test]
  public async Task NoModel_PinsNothingAsync() {
    await Assert.That(KeepSchemaObjectDiscovery.From(null)).IsEmpty();
  }
}
