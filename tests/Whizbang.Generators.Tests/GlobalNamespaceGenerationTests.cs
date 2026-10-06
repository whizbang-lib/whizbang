// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Types declared in the global namespace have no namespace to register or to generate into, and
/// the generators that would otherwise use it say what they do instead.
/// </summary>
/// <tests>src/Whizbang.Generators/EventNamespaceRegistryGenerator.cs</tests>
/// <tests>src/Whizbang.Generators/PerspectiveAccessorGenerator.cs</tests>
[Category("SourceGenerators")]
public class GlobalNamespaceGenerationTests {
  /// <summary>
  /// A receptor of an event in the global namespace contributes no receptor namespace, while one in
  /// a namespace contributes that namespace.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task EventNamespaceRegistry_ReceptorOfAGlobalEvent_RegistersNoNamespaceAsync() {
    var result = GeneratorTestHelper.RunGenerator<EventNamespaceRegistryGenerator>("""
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;

      public record GlobalEvent : IEvent;

      public class GlobalReceptor : IReceptor<GlobalEvent> {
        public ValueTask HandleAsync(GlobalEvent message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
      }

      namespace Shop.Events {
        public record OrderShipped : IEvent;

        public class OrderShippedReceptor : IReceptor<OrderShipped> {
          public ValueTask HandleAsync(OrderShipped message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        }
      }
      """);
    var source = GeneratorTestHelper.GetGeneratedSource(result, "EventNamespaceSource.g.cs");

    await Assert.That(source).IsNotNull();
    await Assert.That(source).Contains("Discovered 0 perspective namespace(s) and 1 receptor namespace(s).");
    await Assert.That(source).Contains("\"shop.events\"");
    await Assert.That(source).DoesNotContain("global namespace");
  }

  /// <summary>
  /// The accessors for a model in the global namespace are generated into the fallback namespace
  /// <c>Whizbang.Generated</c>, and still name the model through <c>global::</c>.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task PerspectiveAccessor_GlobalModel_UsesTheFallbackNamespaceAsync() {
    const string source = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      public record GlobalEvent : IEvent { [StreamId] public Guid Id { get; init; } }
      public record GlobalModel { [StreamId] public Guid Id { get; init; } }
      public class GlobalPerspective : IPerspectiveFor<GlobalModel, GlobalEvent> {
        public GlobalModel Apply(GlobalModel current, GlobalEvent @event) => current;
      }
      """;
    var result = GeneratorTestHelper.RunGenerator<PerspectiveAccessorGenerator>(source);
    var accessors = GeneratorTestHelper.GetGeneratedSource(result, "GlobalModelAccessors.g.cs");

    await Assert.That(accessors).IsNotNull();
    await Assert.That(accessors).Contains("namespace Whizbang.Generated;");
    await Assert.That(accessors).Contains("Expression<Func<global::GlobalModel, global::System.Guid>> Id");
    await Assert.That(GeneratorTestHelper.GetGeneratedCompilationErrors<PerspectiveAccessorGenerator>(source)).IsEmpty();
  }
}
