// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions.Extensions;
using DictionaryMemberDiscovery = global::Whizbang.Generators.Shared.Models.DictionaryMemberDiscovery;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Every closed dictionary type a perspective model holds is found, at any depth, and registered by the generated
/// schema code so both writers of a document store it as an array of key/value objects (#1911).
/// </summary>
/// <docs>fundamentals/perspectives/dictionary-members</docs>
/// <code-under-test>src/Whizbang.Generators.Shared/Models/DictionaryMemberDiscovery.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/PerspectiveSchemaGenerator.cs</code-under-test>
public class DictionaryMemberDiscoveryTests {
  private const string SOURCE = """
    using System;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    namespace TestApp;

    public class Line { public string Name { get; set; } = ""; public Dictionary<string, decimal> Totals { get; set; } = new(); }
    public class Base { public IReadOnlyDictionary<string, string> Notes { get; set; } = new Dictionary<string, string>(); }
    public class Node { public List<Node> Children { get; set; } = new(); public IDictionary<int, bool> Flags { get; set; } = new Dictionary<int, bool>(); }
    public class Holder<T> { public T Value { get; set; } = default!; }

    public class Model : Base {
      public Dictionary<string, int> Counts { get; set; } = new();
      public Dictionary<Guid, Line>? Lines { get; set; }
      public Line[] Rows { get; set; } = Array.Empty<Line>();
      public Node Tree { get; set; } = new();
      public Dictionary<string, Dictionary<string, int>> Nested { get; set; } = new();
      public Dictionary<string, int> CountsAgain { get; set; } = new();
      [System.Text.Json.Serialization.JsonIgnore] public Dictionary<string, Guid> Ignored { get; set; } = new();
      public static Dictionary<string, long> Shared { get; set; } = new();
    }

    public record Noted : IEvent;

    public class ModelPerspective : IPerspectiveFor<Model, Noted> {
      public Model Apply(Model currentData, Noted eventData) => currentData;
    }
    """;

  private static readonly string[] _expected = [
    "string -> int",
    "global::System.Guid -> global::TestApp.Line",
    "string -> decimal",
    "int -> bool",
    "string -> global::System.Collections.Generic.Dictionary<string, int>",
    "string -> string",
  ];

  private static IReadOnlyList<(string KeyType, string ValueType)> _found(string metadataName) {
    // The serializer's assembly too, so [JsonIgnore] resolves to the real attribute the walk recognizes.
    var compilation = GeneratorTestHelper.CreateCompilation(SOURCE).AddReferences(
      SharedMetadataReferences.FromFile(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute).Assembly.Location));
    return DictionaryMemberDiscovery.From(compilation.GetTypeByMetadataName(metadataName));
  }

  [Test]
  public async Task EveryStoredDictionary_AtAnyDepth_IsFoundOnceAsync() {
    var found = _found("TestApp.Model").Select(f => $"{f.KeyType} -> {f.ValueType}").ToList();

    await Assert.That(string.Join(" | ", found.Order(StringComparer.Ordinal))).IsEqualTo(string.Join(" | ", _expected.Order(StringComparer.Ordinal))).Because("a member, a dictionary's value, an array element, a collection element, a nested object, an inherited "
      + "member and a dictionary held as a value are all stored; an ignored or static member is not");
  }

  [Test]
  public async Task NoModel_HoldsNothingAsync() {
    await Assert.That(DictionaryMemberDiscovery.From(null)).IsEmpty();
    await Assert.That(_found("TestApp.Holder`1")).IsEmpty()
      .Because("a type parameter is no type the walk can enter");
  }

  [Test]
  [Arguments("System.Collections.Generic.Dictionary`2", true)]
  [Arguments("System.Collections.Generic.IDictionary`2", true)]
  [Arguments("System.Collections.Generic.IReadOnlyDictionary`2", true)]
  [Arguments("System.Collections.Generic.KeyValuePair`2", false)]
  [Arguments("System.Collections.Generic.List`1", false)]
  public async Task TheDeclaredShapes_AreTheDictionariesAsync(string metadataName, bool expected) {
    var compilation = GeneratorTestHelper.CreateCompilation(SOURCE);
    var definition = compilation.GetTypeByMetadataName(metadataName)!;
    var intType = compilation.GetSpecialType(Microsoft.CodeAnalysis.SpecialType.System_Int32);
    var constructed = definition.Construct([.. Enumerable.Repeat<Microsoft.CodeAnalysis.ITypeSymbol>(intType, definition.Arity)]);

    await Assert.That(DictionaryMemberDiscovery.IsDictionary(constructed)).IsEqualTo(expected);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task TheSchemaGenerator_RegistersEachFormOnce_AndTheRegistrationCompilesAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(SOURCE);

    var generated = GeneratorTestHelper.GetRequiredGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    var registrations = generated.Split('\n')
      .Where(l => l.Contains("DictionaryMemberForms.Register<", StringComparison.Ordinal))
      .Select(l => l.Trim())
      .ToList();

    await Assert.That(registrations).Contains("global::Whizbang.Core.Perspectives.DictionaryMemberForms.Register<string, int>();");
    await Assert.That(registrations).Contains("global::Whizbang.Core.Perspectives.DictionaryMemberForms.Register<global::System.Guid, global::TestApp.Line>();");
    await Assert.That(registrations.Count).IsEqualTo(6);
    await Assert.That(result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)).IsEmpty();
  }

  /// <summary>
  /// An enumeration that only keys a dictionary still gets a type info: the stored form writes each key through its
  /// own metadata, so an enumeration key with none would fail the first write.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AnEnumerationKeyingADictionary_HasItsOwnTypeInfoAsync() {
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>("""
      using System.Collections.Generic;
      using Whizbang.Core;

      namespace TestApp;

      public enum Shade { Light, Dark }
      public enum Tone { Warm, Cool }

      public record Painted : IEvent {
        public Dictionary<Shade, Tone> ByShade { get; init; } = new();
      }
      """);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs")!;

    await Assert.That(code).Contains("GetEnumConverter<global::TestApp.Shade>");
    await Assert.That(code).Contains("GetEnumConverter<global::TestApp.Tone>");
    await Assert.That(code).Contains("_registered<global::System.Collections.Generic.Dictionary<global::TestApp.Shade, global::TestApp.Tone>>(options)")
      .Because("a converter registered on the options (the persistence profile's pairs) answers before the built-in dictionary form");
  }
}
