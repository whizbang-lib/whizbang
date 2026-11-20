// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The per-type stream-id table (#1905, #1908): each assembly registers one entry per message type it compiles, keyed by
/// the exact type and holding that type's effective stream id and generation policy, so a message resolves by its
/// runtime type wherever its base types are compiled. The runtime tests compile two or three assemblies with the
/// generator, load them into a collectible context, and read what the registry answers for the derived types.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/StreamIdGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/Utilities/StreamIdSelection.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Registry/StreamIdTypeRegistry.cs</code-under-test>
[Category("SourceGenerators")]
public class StreamIdTypeTableGeneratorTests {
  private const string EXTRACTORS = "StreamIdExtractors.g.cs";
  private const string ENTRY = "new global::Whizbang.Core.Registry.StreamIdTypeEntry(typeof(";

  private static readonly Guid _baseKey = Guid.Parse("01890a5d-ac96-774b-bcce-b302099a1905");
  private static readonly Guid _ownKey = Guid.Parse("01890a5d-ac96-774b-bcce-b302099a1908");
  private static readonly Guid _written = Guid.Parse("01890a5d-ac96-774b-bcce-b302099a0001");

  // ========================================
  // The generated table
  // ========================================

  /// <summary>Every event, command and composite gets an entry, including a type that only inherits its stream id.</summary>
  [Test]
  public async Task EveryMessageType_IncludingAnInheritingOne_GetsAnEntryAsync() {
    const string source = """
      using System;
      using Whizbang.Core;
      namespace App;
      public class OrderEvent : IEvent { [StreamId] public Guid OrderId { get; set; } }
      public class OrderShipped : OrderEvent { }
      public class PlaceOrder : ICommand { [StreamId] public Guid OrderId { get; set; } }
      """;

    var code = GeneratorTestHelper.GetRequiredGeneratedSource(GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source), EXTRACTORS);

    await Assert.That(code).Contains("global::Whizbang.Core.Registry.StreamIdTypeRegistry.Register(");
    await Assert.That(code).Contains(ENTRY + "global::App.OrderEvent)");
    await Assert.That(code).Contains(ENTRY + "global::App.OrderShipped)");
    await Assert.That(code).Contains(ENTRY + "global::App.PlaceOrder)");
  }

  /// <summary>The table is the same whatever order the types are declared in, so the generated file is deterministic.</summary>
  [Test]
  public async Task TheTable_DoesNotDependOnDeclarationOrderAsync() {
    const string first = "public class Alpha : IEvent { [StreamId] public Guid Id { get; set; } }";
    const string second = "public class Beta : Alpha { }";
    const string third = "public class Gamma : ICommand { [StreamId] public Guid Id { get; set; } }";

    var forward = _table($"{first}\n{second}\n{third}");
    var backward = _table($"{third}\n{second}\n{first}");

    await Assert.That(forward).Contains(ENTRY);
    await Assert.That(backward).IsEqualTo(forward);
  }

  private static string _table(string types) {
    var code = GeneratorTestHelper.GetRequiredGeneratedSource(GeneratorTestHelper.RunGenerator<StreamIdGenerator>($"""
      using System;
      using Whizbang.Core;
      namespace App;
      {types}
      """), EXTRACTORS);
    var start = code.IndexOf("StreamIdTypeRegistry.Register(", StringComparison.Ordinal);
    return start < 0 ? "" : code[start..code.IndexOf(");", start, StringComparison.Ordinal)];
  }

  // ========================================
  // Across assemblies (#1905)
  // ========================================

  private const string BASE_ASSEMBLY = """
    using System;
    using Whizbang.Core;
    namespace Shared {
      [GenerateStreamId]
      public class OrderEvent : IEvent { [StreamId] public Guid Key { get; set; } }
    }
    """;

  /// <summary>
  /// A derived type in another assembly with its own <c>[StreamId]</c> resolves and sets its own property, and does not
  /// inherit the base's <c>[GenerateStreamId]</c> (#1893's rule, which used to hold only within one assembly).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ADerivedTypeInAnotherAssemblyWithItsOwnStreamId_UsesItsOwnPropertyAndPolicyAsync() {
    var observed = _runAcross("OwnAcross", """
      public class LineAdded : Shared.OrderEvent { [StreamId] public Guid LineId { get; set; } }
      """, """
      var message = new LineAdded { Key = BaseKey, LineId = OwnKey };
      var extracted = StreamIdExtractorRegistry.ExtractStreamId(message, typeof(LineAdded));
      var policy = StreamIdExtractorRegistry.GetGenerationPolicy(message);
      var wrote = StreamIdExtractorRegistry.SetStreamId(message, Written);
      return $"{extracted};{policy.ShouldGenerate};{wrote};{message.LineId};{message.Key}";
      """, BASE_ASSEMBLY);

    await Assert.That(observed).IsEqualTo($"{_ownKey};False;True;{_written};{_baseKey}");
  }

  /// <summary>A derived type in another assembly with no <c>[StreamId]</c> of its own inherits the base's, policy included.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ADerivedTypeInAnotherAssemblyWithNoStreamIdOfItsOwn_InheritsTheBasesAsync() {
    var observed = _runAcross("InheritAcross", """
      public class OrderShipped : Shared.OrderEvent { public string Carrier { get; set; } = ""; }
      """, """
      var message = new OrderShipped { Key = BaseKey };
      var extracted = StreamIdExtractorRegistry.ExtractStreamId(message, typeof(OrderShipped));
      var policy = StreamIdExtractorRegistry.GetGenerationPolicy(message);
      return $"{extracted};{policy.ShouldGenerate}";
      """, BASE_ASSEMBLY);

    await Assert.That(observed).IsEqualTo($"{_baseKey};True");
  }

  /// <summary>
  /// Three levels in three assemblies: the leaf, with no stream id of its own, takes the middle level's, which declared
  /// its own and so declined the root's generation.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AChainSpanningThreeAssemblies_ResolvesTheNearestLevelAsync() {
    var root = _compile("ChainRoot", BASE_ASSEMBLY, []);
    var middle = _compile("ChainMiddle", """
      using System;
      using Whizbang.Core;
      namespace Middle {
        public class LineEvent : Shared.OrderEvent { [StreamId] public Guid LineId { get; set; } }
      }
      """, [MetadataReference.CreateFromImage(root)]);
    var observed = _runAcross("ChainLeaf", """
      public class LineRepriced : Middle.LineEvent { public decimal Price { get; set; } }
      """, """
      var message = new LineRepriced { Key = BaseKey, LineId = OwnKey };
      var extracted = StreamIdExtractorRegistry.ExtractStreamId(message, typeof(LineRepriced));
      var policy = StreamIdExtractorRegistry.GetGenerationPolicy(message);
      return $"{extracted};{policy.ShouldGenerate}";
      """, null, root, middle);

    await Assert.That(observed).IsEqualTo($"{_ownKey};False");
  }

  // ========================================
  // Interfaces (#1908)
  // ========================================

  /// <summary>A type whose stream id comes from one interface resolves through the property that implements it.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AStreamIdFromOneInterface_ResolvesAsync() {
    var observed = _runAcross("OneInterface", """
      public interface IOrderKeyed { [StreamId] Guid OrderId { get; } }
      public class OrderNoted : IEvent, IOrderKeyed { public Guid OrderId { get; set; } }
      """, """
      var message = new OrderNoted { OrderId = OwnKey };
      return $"{StreamIdExtractorRegistry.ExtractStreamId(message, typeof(OrderNoted))}";
      """, null);

    await Assert.That(observed).IsEqualTo($"{_ownKey}");
  }

  /// <summary>Two interfaces naming the same property are one source, not an ambiguity.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task TwoInterfacesNamingTheSameProperty_ResolveAsync() {
    var observed = _runAcross("SameProperty", """
      public interface IOrderKeyed { [StreamId] Guid OrderId { get; } }
      public interface IAlsoOrderKeyed { [StreamId] Guid OrderId { get; } }
      public class OrderNoted : IEvent, IOrderKeyed, IAlsoOrderKeyed { public Guid OrderId { get; set; } }
      """, """
      var message = new OrderNoted { OrderId = OwnKey };
      return $"{StreamIdExtractorRegistry.ExtractStreamId(message, typeof(OrderNoted))}";
      """, null);

    await Assert.That(observed).IsEqualTo($"{_ownKey}");
  }

  /// <summary>Two interfaces naming different properties are ambiguous: the type gets no entry (and WHIZ036 says why).</summary>
  [Test]
  public async Task TwoInterfacesNamingDifferentProperties_GetNoEntryAsync() {
    const string source = """
      using System;
      using Whizbang.Core;
      namespace App;
      public interface IOrderKeyed { [StreamId] Guid OrderId { get; } }
      public interface ICustomerKeyed { [StreamId] Guid CustomerId { get; } }
      public class Ambiguous : IEvent, IOrderKeyed, ICustomerKeyed { public Guid OrderId { get; set; } public Guid CustomerId { get; set; } }
      public class Settled : IEvent, IOrderKeyed, ICustomerKeyed {
        public Guid OrderId { get; set; }
        public Guid CustomerId { get; set; }
        [StreamId] public Guid SettledId { get; set; }
      }
      """;

    var code = GeneratorTestHelper.GetRequiredGeneratedSource(GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source), EXTRACTORS);

    await Assert.That(code).DoesNotContain(ENTRY + "global::App.Ambiguous)");
    await Assert.That(code).Contains(ENTRY + "global::App.Settled)")
      .Because("a type that declares its own [StreamId] over an ambiguous pair resolves by its own");
  }

  // ========================================
  // Harness
  // ========================================

  /// <summary>
  /// Compiles <paramref name="types"/> and a probe into <paramref name="assemblyName"/>, against the given assemblies
  /// (and against <paramref name="baseSource"/> compiled first, when given), loads them all into a collectible context
  /// and returns what <c>App.Probe.Run()</c> returned.
  /// </summary>
  [RequiresAssemblyFiles]
  private static string _runAcross(string assemblyName, string types, string probe, string? baseSource, params byte[][] references) {
    var images = references.ToList();
    if (baseSource is not null) {
      images.Add(_compile(assemblyName + "Base", baseSource, []));
    }
    var app = _compile(assemblyName, $$"""
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Registry;
      namespace App {
        {{types}}
        public static class Probe {
          private static readonly Guid BaseKey = new("{{_baseKey}}");
          private static readonly Guid OwnKey = new("{{_ownKey}}");
          private static readonly Guid Written = new("{{_written}}");
          public static string Run() {
            {{probe}}
          }
        }
      }
      """, [.. images.Select(i => MetadataReference.CreateFromImage(i))]);

    var context = new System.Runtime.Loader.AssemblyLoadContext(assemblyName, isCollectible: true);
    try {
      foreach (var image in images) {
        context.LoadFromStream(new MemoryStream(image));
      }
      var assembly = context.LoadFromStream(new MemoryStream(app));
      return (string)assembly.GetType("App.Probe", throwOnError: true)!.GetMethod("Run")!.Invoke(null, null)!;
    } finally {
      context.Unload();
    }
  }

  /// <summary>Compiles an assembly with the stream-id generator and returns its image.</summary>
  [RequiresAssemblyFiles]
  private static byte[] _compile(string assemblyName, string source, IEnumerable<MetadataReference> references) {
    var (_, output) = GeneratorTestHelper.RunGeneratorsWithFullReferences([new StreamIdGenerator()], source, assemblyName, references);
    using var image = new MemoryStream();
    var emitted = output.Emit(image);
    if (!emitted.Success) {
      throw new InvalidOperationException(
        $"{assemblyName} does not compile: "
        + string.Join(Environment.NewLine, emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
    }
    return image.ToArray();
  }
}
