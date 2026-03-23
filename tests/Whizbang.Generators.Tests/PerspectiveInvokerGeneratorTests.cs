using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for PerspectiveInvokerGenerator.
/// Verifies invoker generation includes IPerspectiveWithActionsFor perspectives.
/// </summary>
[Category("SourceGenerators")]
public class PerspectiveInvokerGeneratorTests {

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_IPerspectiveWithActionsFor_GeneratesInvokerAsync() {
    // Arrange — Perspective using only IPerspectiveWithActionsFor
    const string source = """
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestApp;

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
    var result = GeneratorTestHelper.RunGenerator<PerspectiveInvokerGenerator>(source);

    // Assert — Invoker must be generated for WithActionsFor perspective
    var code = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveInvoker.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code).Contains("DeletedEvent")
      .Because("IPerspectiveWithActionsFor events must be included in perspective invoker");
  }
}
