using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for EventNamespaceRegistryGenerator.
/// Verifies event namespace routing includes IPerspectiveWithActionsFor events.
/// </summary>
[Category("SourceGenerators")]
public class EventNamespaceRegistryGeneratorTests {

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_IPerspectiveWithActionsFor_IncludesEventNamespaceAsync() {
    // Arrange — EXACT same source as passing PerspectiveRunnerRegistryGenerator test
    const string source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestApp.Events {
  public record DeletedEvent : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public record Model {
    [StreamId]
    public Guid Id { get; init; }
  }

  public class PurgeOnlyPerspective : IPerspectiveWithActionsFor<Model, DeletedEvent> {
    public ApplyResult<Model> Apply(Model current, DeletedEvent @event)
        => ApplyResult<Model>.Purge();
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<EventNamespaceRegistryGenerator>(source);

    // Assert — Event namespace must be included for routing
    var code = GeneratorTestHelper.GetGeneratedSource(result, "EventNamespaceSource.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code).Contains("testapp.events")
      .Because("IPerspectiveWithActionsFor event namespaces must be included in routing registry (lowercased)");
  }

}
