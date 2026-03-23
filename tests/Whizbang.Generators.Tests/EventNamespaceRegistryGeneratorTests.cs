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
    // Arrange — Perspective using only IPerspectiveWithActionsFor
    const string source = """
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestApp.Events;

public record DeletedEvent : IEvent {
  [StreamId]
  public Guid Id { get; init; }
}

public record OrderModel {
  [StreamId]
  public Guid Id { get; init; }
}

public class OrderPurgePerspective : IPerspectiveWithActionsFor<OrderModel, DeletedEvent> {
  public ApplyResult<OrderModel> Apply(OrderModel current, DeletedEvent @event)
    => ApplyResult<OrderModel>.Purge();
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<EventNamespaceRegistryGenerator>(source);

    // Assert — Event namespace must be included for routing
    var code = GeneratorTestHelper.GetGeneratedSource(result, "EventNamespaceSource.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code).Contains("TestApp.Events")
      .Because("IPerspectiveWithActionsFor event namespaces must be included in routing registry");
  }
}
