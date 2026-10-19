// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Build diagnostics are reported at the declaration they are about, so a developer can suppress one where it appears
/// and the IDE shows it there (#1429). Each test names the text the diagnostic must point at.
/// </summary>
[Category("Diagnostics")]
public class DiagnosticLocationTests {
  private const string PERSPECTIVE = """
    using System;
    using Whizbang.Core;
    using Whizbang.Core.Lenses;
    using Whizbang.Core.Perspectives;

    namespace Shop;

    public record OrderPlaced : IEvent { [StreamId] public Guid OrderId { get; init; } }
    public record OrderView { [StreamId] public Guid Id { get; init; } }
    public class OrderViewPerspective : IPerspectiveFor<OrderView, OrderPlaced> {
      public OrderView Apply(OrderView current, OrderPlaced @event) => current with { Id = @event.OrderId };
    }
    """;

  /// <summary>WHIZ013 points at the init-only [StreamId] property.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Whiz013_IsReportedAtThePropertyAsync() {
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>("""
      using System;
      using Whizbang.Core;
      namespace Shop;
      public record OrderPlaced : IEvent {
        [StreamId]
        [GenerateStreamId]
        public Guid OrderId { get; init; }
      }
      """);

    await Assert.That(DiagnosticSourceText.Of(result.Diagnostics.Single(d => d.Id == "WHIZ013"))).IsEqualTo("OrderId");
  }

  /// <summary>WHIZ024 points at a [WhizbangId] attribute of the colliding pair.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Whiz024_IsReportedAtTheAttributeAsync() {
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>("""
      using Whizbang.Core;
      namespace Shop.Orders { [WhizbangId] public readonly partial struct LineId { } }
      namespace Shop.Invoices { [WhizbangId] public readonly partial struct LineId { } }
      """);

    await Assert.That(DiagnosticSourceText.Of(result.Diagnostics.Single(d => d.Id == "WHIZ024"))).IsEqualTo("WhizbangId");
  }

  /// <summary>WHIZ033 points at the perspective's name in the syntax tree, not at an external-file location.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Whiz033_IsReportedAtThePerspectiveInTheSyntaxTreeAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>("""
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      namespace Shop;
      public record OrderPlaced : IEvent { [StreamId] public Guid OrderId { get; init; } }
      public record NoIdView { public string Name { get; init; } = ""; }
      public class NoIdPerspective : IPerspectiveFor<NoIdView, OrderPlaced> {
        public NoIdView Apply(NoIdView current, OrderPlaced @event) => current;
      }
      """);

    await Assert.That(DiagnosticSourceText.Of(result.Diagnostics.Single(d => d.Id == "WHIZ033"))).IsEqualTo("NoIdPerspective");
  }

  /// <summary>
  /// The informational "discovered" and "generated" diagnostics stay without a location. With one, a code-quality
  /// tool imports each as an issue on every message type and perspective, in this repository and in a consumer's.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task InformationalDiagnostics_HaveNoLocationAsync() {
    var services = """
      using System;
      using System.Linq;
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core.Lenses;
      namespace Shop;
      public record Order(Guid Id);
      public interface IOrderLens : ILensQuery<Order> { }
      public abstract class BaseOrderLens : IOrderLens {
        public abstract IQueryable<PerspectiveRow<Order>> Query { get; }
        public abstract Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default);
      }
      public class OrderLens : BaseOrderLens {
        public override IQueryable<PerspectiveRow<Order>> Query => throw new NotImplementedException();
        public override Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Order?>(null);
      }
      """;
    var reported = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(PERSPECTIVE).Diagnostics
      .Concat(GeneratorTestHelper.RunGenerator<PerspectiveInvokerGenerator>(PERSPECTIVE).Diagnostics)
      .Concat(GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(PERSPECTIVE).Diagnostics)
      .Concat(GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(PERSPECTIVE).Diagnostics)
      .Concat(GeneratorTestHelper.RunGenerator<ServiceRegistrationGenerator>(services).Diagnostics)
      .Where(static d => d.Id is "WHIZ007" or "WHIZ011" or "WHIZ012" or "WHIZ027" or "WHIZ040" or "WHIZ041")
      .ToList();

    await Assert.That(reported.Select(static d => d.Id).Distinct().Order(StringComparer.Ordinal))
      .IsEquivalentTo(["WHIZ007", "WHIZ011", "WHIZ012", "WHIZ027", "WHIZ040", "WHIZ041"])
      .Because("every one of them must be reported for the check below to cover it");
    await Assert.That(reported.Where(static d => d.Location.IsInSource).Select(static d => d.Id)).IsEmpty();
  }
}
