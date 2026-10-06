// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// A message property of any recognized collection type contributes its element type (a
/// dictionary's value type) to the generated context, and never the collection type itself: the
/// collection is under <c>System.</c>, which the direct-property path refuses before anything else.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/MessageJsonContextGenerator.cs</code-under-test>
public class MessageJsonContextCollectionPropertyTests {
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_EveryCollectionForm_DiscoversItsElementTypeAsync() {
    const string source = """
      using System.Collections.Generic;
      using Whizbang.Core;

      namespace TestApp;

      public record ItemA { public string V { get; init; } = ""; }
      public record ItemB { public string V { get; init; } = ""; }
      public record ItemC { public string V { get; init; } = ""; }
      public record ItemD { public string V { get; init; } = ""; }
      public record ItemE { public string V { get; init; } = ""; }
      public record ItemF { public string V { get; init; } = ""; }
      public record ItemG { public string V { get; init; } = ""; }
      public record ItemH { public string V { get; init; } = ""; }
      public record ItemI { public string V { get; init; } = ""; }

      public record CollectionsEvent : IEvent {
        public List<ItemA> A { get; init; } = [];
        public IList<ItemB> B { get; init; } = [];
        public IReadOnlyList<ItemC> C { get; init; } = [];
        public ICollection<ItemD> D { get; init; } = [];
        public IReadOnlyCollection<ItemE> E { get; init; } = [];
        public IEnumerable<ItemF> F { get; init; } = [];
        public Dictionary<string, ItemG> G { get; init; } = [];
        public IDictionary<string, ItemH> H { get; init; } = new Dictionary<string, ItemH>();
        public IReadOnlyDictionary<string, ItemI> I { get; init; } = new Dictionary<string, ItemI>();
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    foreach (var item in new[] { "ItemA", "ItemB", "ItemC", "ItemD", "ItemE", "ItemF", "ItemG", "ItemH", "ItemI" }) {
      await Assert.That(code!).Contains($"global::TestApp.{item}")
        .Because($"{item} is reachable only as a collection's element, so element extraction must find it");
    }
  }
}
