// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// A generator that sorts names emits them in ordinal order, never in the build machine's culture order (#2484).
/// </summary>
/// <remarks>
/// <para>
/// A string sort with no comparer uses the current culture, which differs between build machines (a Danish
/// machine sorts "aa" after "z"), so two builds of one commit could emit the same members in two orders and produce
/// two different assemblies. Ordinal order is the same everywhere.
/// </para>
/// <para>
/// Each test declares the same two types in both orders, requires the same output either way, and requires
/// <c>Zeta</c> before <c>alpha</c>: ordinal order puts an upper-case letter before every lower-case one, while every
/// culture order puts "alpha" first. A culture-ordered sort therefore fails the test on any machine.
/// </para>
/// </remarks>
[Category("SourceGenerators")]
public class OrdinalEmitOrderTests {
  private static async Task _assertOrdinalAsync(string? zetaFirst, string? alphaFirst, string zetaMarker, string alphaMarker) {
    await Assert.That(zetaFirst).IsNotNull();
    await Assert.That(alphaFirst).IsEqualTo(zetaFirst);
    var zeta = zetaFirst!.IndexOf(zetaMarker, StringComparison.Ordinal);
    var alpha = zetaFirst.IndexOf(alphaMarker, StringComparison.Ordinal);
    await Assert.That(zeta).IsGreaterThanOrEqualTo(0);
    await Assert.That(alpha).IsGreaterThanOrEqualTo(0);
    await Assert.That(zeta).IsLessThan(alpha).Because("ordinal order puts Zeta before alpha; a culture order would not");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task TopicRegistry_EmitsTopicsInOrdinalOrderAsync() {
    static string Source(string first, string second) => $$"""
using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace TestApp;

{{first}}
{{second}}
""";
    const string zeta = "[Topic(\"zeta-topic\")] public record ZetaEvent : IEvent;";
    const string alpha = "[Topic(\"alpha-topic\")] public record alphaEvent : IEvent;";

    var a = GeneratorTestHelper.GetGeneratedSource(GeneratorTestHelper.RunGenerator<TopicRegistryGenerator>(Source(zeta, alpha)), "TopicRegistry.g.cs");
    var b = GeneratorTestHelper.GetGeneratedSource(GeneratorTestHelper.RunGenerator<TopicRegistryGenerator>(Source(alpha, zeta)), "TopicRegistry.g.cs");

    await _assertOrdinalAsync(a, b, "typeof(global::TestApp.ZetaEvent)", "typeof(global::TestApp.alphaEvent)");
  }

  private static string _perspectiveSource(string first, string second) => $$"""
using System;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestApp;

public record ZetaEvent : IEvent {
  [StreamId]
  public Guid Id { get; init; }
}

public record alphaEvent : IEvent {
  [StreamId]
  public Guid Id { get; init; }
}

public record ZetaModel {
  [StreamId]
  public Guid Id { get; init; }
}

public record alphaModel {
  [StreamId]
  public Guid Id { get; init; }
}

{{first}}
{{second}}
""";

  private const string ZETA_PERSPECTIVE = """
public class ZetaPerspective : IPerspectiveFor<ZetaModel, ZetaEvent> {
  public ZetaModel Apply(ZetaModel current, ZetaEvent @event) => current;
}
""";

  private const string ALPHA_PERSPECTIVE = """
public class alphaPerspective : IPerspectiveFor<alphaModel, alphaEvent> {
  public alphaModel Apply(alphaModel current, alphaEvent @event) => current;
}
""";

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerRegistry_EmitsRunnerLookupInOrdinalOrderAsync() {
    var a = GeneratorTestHelper.GetGeneratedSource(
        GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(_perspectiveSource(ZETA_PERSPECTIVE, ALPHA_PERSPECTIVE)), "PerspectiveRunnerRegistry.g.cs");
    var b = GeneratorTestHelper.GetGeneratedSource(
        GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(_perspectiveSource(ALPHA_PERSPECTIVE, ZETA_PERSPECTIVE)), "PerspectiveRunnerRegistry.g.cs");

    // The runner lookup switch and the registration list are both ordered by the perspective's CLR name.
    await _assertOrdinalAsync(a, b, "\"TestApp.ZetaPerspective\" =>", "\"TestApp.alphaPerspective\" =>");
    var full = a!;
    var registered = full[full.IndexOf("_registeredPerspectives", StringComparison.Ordinal)..];
    await _assertOrdinalAsync(registered, registered, "\"TestApp.ZetaPerspective\",", "\"TestApp.alphaPerspective\",");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerRegistry_RegistersPerspectivesInOrdinalOrderAsync() {
    var a = GeneratorTestHelper.GetGeneratedSource(
        GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(_perspectiveSource(ZETA_PERSPECTIVE, ALPHA_PERSPECTIVE)), "PerspectiveRunnerRegistry.g.cs");
    var b = GeneratorTestHelper.GetGeneratedSource(
        GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(_perspectiveSource(ALPHA_PERSPECTIVE, ZETA_PERSPECTIVE)), "PerspectiveRunnerRegistry.g.cs");

    // The perspective classes are registered by simple name.
    await _assertOrdinalAsync(a, b, "services.AddScoped<global::TestApp.ZetaPerspective>()", "services.AddScoped<global::TestApp.alphaPerspective>()");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveInvoker_RoutesEventsInOrdinalOrderAsync() {
    var a = GeneratorTestHelper.GetGeneratedSource(
        GeneratorTestHelper.RunGenerator<PerspectiveInvokerGenerator>(_perspectiveSource(ZETA_PERSPECTIVE, ALPHA_PERSPECTIVE)), "PerspectiveInvoker.g.cs");
    var b = GeneratorTestHelper.GetGeneratedSource(
        GeneratorTestHelper.RunGenerator<PerspectiveInvokerGenerator>(_perspectiveSource(ALPHA_PERSPECTIVE, ZETA_PERSPECTIVE)), "PerspectiveInvoker.g.cs");

    await _assertOrdinalAsync(a, b, "TestApp.ZetaEvent", "TestApp.alphaEvent");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageTagHookDispatcher_ListsAttributesInOrdinalOrderAsync() {
    static string Source(string first, string second) => $$"""
using System;
using Whizbang.Core.Attributes;

namespace TestApp;

{{first}}
{{second}}

[ZetaTag("z")]
public record ZetaCreated(Guid Id);

[alphaTag("a")]
public record alphaCreated(Guid Id);
""";
    const string zeta = "public class ZetaTagAttribute : MessageTagAttribute { public ZetaTagAttribute(string tag) { Tag = tag; } }";
    const string alpha = "public class alphaTagAttribute : MessageTagAttribute { public alphaTagAttribute(string tag) { Tag = tag; } }";

    var a = GeneratorTestHelper.GetGeneratedSource(GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(Source(zeta, alpha)), "MessageTagHookDispatcher.g.cs");
    var b = GeneratorTestHelper.GetGeneratedSource(GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(Source(alpha, zeta)), "MessageTagHookDispatcher.g.cs");

    await _assertOrdinalAsync(a, b, "TestApp.ZetaTagAttribute", "TestApp.alphaTagAttribute");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistry_ListsReferencedWhizbangPackagesInOrdinalOrderAsync() {
    // The registry names each referenced Whizbang assembly as a package; reference two in both orders.
    static MetadataReference Assembly(string name) =>
      CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText("public class Marker {}")],
        [SharedMetadataReferences.FromFile(typeof(object).Assembly.Location)],
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).ToMetadataReference();
    static string Run(MetadataReference first, MetadataReference second) {
      var compilation = CSharpCompilation.Create("TestAssembly",
        [CSharpSyntaxTree.ParseText("public class Consumer {}")],
        [SharedMetadataReferences.FromFile(typeof(object).Assembly.Location), first, second],
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      var result = CSharpGeneratorDriver.Create([new MessageRegistryGenerator().AsSourceGenerator()], additionalTexts: null, parseOptions: null, optionsProvider: null)
        .RunGenerators(compilation).GetRunResult();
      return GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs")!;
    }
    var zeta = Assembly("Whizbang.Zeta");
    var alpha = Assembly("Whizbang.alpha");

    await _assertOrdinalAsync(Run(zeta, alpha), Run(alpha, zeta), "SoftwareExtravaganza.Whizbang.Zeta", "SoftwareExtravaganza.Whizbang.alpha");
  }
}
