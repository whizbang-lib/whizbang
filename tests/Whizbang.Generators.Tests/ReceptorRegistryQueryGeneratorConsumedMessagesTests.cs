// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The generated contribution enumerates what the assembly consumes (#2058): every type in AnyConsumerTypes, in the
/// assembly-qualified wire form an origin's event store holds, marked event or not. The list and the receive
/// boundary's predicate are built from the same discovery, so they cannot disagree.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/ReceptorRegistryQueryGenerator.cs</code-under-test>
[Category("SourceGenerators")]
[Category("ReceptorRegistryQuery")]
public partial class ReceptorRegistryQueryGeneratorConsumedMessagesTests {

  private const string SOURCE = """
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core {
  public sealed class NotificationTagAttribute : System.Attribute {
    public NotificationTagAttribute(string tag) { }
  }
}

namespace MyApp {
  public record CreateOrder : ICommand { public string OrderId { get; init; } = string.Empty; }
  public record OrderShipped : IEvent { public string OrderId { get; init; } = string.Empty; }
  public record OrderCreated : IEvent { public string OrderId { get; init; } = string.Empty; }
  public record OrderModel { public string Id { get; set; } = string.Empty; }

  public static class Contracts {
    public record Nested : IEvent { public string Id { get; init; } = string.Empty; }
  }

  [Whizbang.Core.NotificationTagAttribute("orders")]
  public record OrderNotice { }

  public record BulkImport : ICompositeEvent {
    public IEnumerable<IMessage> InnerEvents => new IMessage[0];
  }

  public sealed record ArchiveJobs(
    CollectiveScope Scope, IReadOnlyList<System.Guid> MatchedStreamIds) : ICollectiveEvent;

  public class CreateOrderReceptor : IReceptor<CreateOrder> {
    public ValueTask HandleAsync(CreateOrder message, CancellationToken ct = default) => ValueTask.CompletedTask;
  }

  public class OrderShippedReceptor : IReceptor<OrderShipped> {
    public ValueTask HandleAsync(OrderShipped message, CancellationToken ct = default) => ValueTask.CompletedTask;
  }

  public class NestedReceptor : IReceptor<Contracts.Nested> {
    public ValueTask HandleAsync(Contracts.Nested message, CancellationToken ct = default) => ValueTask.CompletedTask;
  }

  public class OrderProjection : IPerspectiveFor<OrderModel, OrderCreated> {
    public ApplyResult<OrderModel> Apply(OrderModel current, OrderCreated @event)
      => ApplyResult<OrderModel>.Update(new OrderModel { Id = @event.OrderId });
  }
}
""";

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_EnumeratesEveryConsumedType_InTheWireForm_MarkedEventOrNotAsync() {
    var consumed = _consumedRegion(_generate());

    await Assert.That(consumed).Contains(_entry("MyApp.OrderShipped, TestAssembly", isEvent: true))
      .Because("an event a receptor handles is consumed");
    await Assert.That(consumed).Contains(_entry("MyApp.OrderCreated, TestAssembly", isEvent: true))
      .Because("an event a perspective folds is consumed");
    await Assert.That(consumed).Contains(_entry("MyApp.CreateOrder, TestAssembly", isEvent: false))
      .Because("a command is consumed, and is not an event");
    await Assert.That(consumed).Contains(_entry("MyApp.Contracts+Nested, TestAssembly", isEvent: true))
      .Because("a nested type's wire form uses '+', the form Type.FullName and the event store use");
    await Assert.That(consumed).Contains(_entry("MyApp.OrderNotice, TestAssembly", isEvent: false))
      .Because("a type a notification tag makes consumed is enumerated too");
    await Assert.That(consumed).Contains(_entry("MyApp.BulkImport, TestAssembly", isEvent: false))
      .Because("a composite is consumed by the fan-out seam, and it is not an event: it is never stored, only its children are");
    await Assert.That(consumed).Contains(_entry("MyApp.ArchiveJobs, TestAssembly", isEvent: true))
      .Because("a collective event is consumed by the collective sink");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_EnumeratesExactlyTheAnyConsumerSet_SoTheListAndThePredicateAgreeAsync() {
    var generated = _generate();
    var anyConsumer = _quoted().Matches(_region(generated, "AnyConsumerTypes"))
      .Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal).ToList();
    var enumerated = _consumedName().Matches(_consumedRegion(generated))
      .Select(m => m.Groups[1].Value.Replace('+', '.')).Order(StringComparer.Ordinal).ToList();

    await Assert.That(anyConsumer).IsNotEmpty();
    await Assert.That(enumerated).IsEquivalentTo(anyConsumer)
      .Because("every type HasAnyConsumer answers true for is enumerable, and nothing else is");
  }

  [GeneratedRegex("\"([^\"]+)\"")]
  private static partial Regex _quoted();

  [GeneratedRegex("ConsumedMessageInfo\\(\"([^\"]+), TestAssembly\"")]
  private static partial Regex _consumedName();

  private static string _generate() {
    var result = GeneratorTestHelper.RunGenerator<ReceptorRegistryQueryGenerator>(SOURCE);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangReceptorRegistryQueryRegistration.g.cs");
    return generated ?? throw new InvalidOperationException(
      "no registration was generated: " + string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
  }

  private static string _entry(string typeName, bool isEvent) =>
    $"new global::Whizbang.Core.Messaging.ConsumedMessageInfo(\"{typeName}\", {(isEvent ? "true" : "false")})";

  private static string _consumedRegion(string generated) => _region(generated, "ConsumedMessages");

  private static string _region(string source, string fieldName) {
    var fieldStart = source.IndexOf(fieldName + " =", StringComparison.Ordinal);
    if (fieldStart < 0) {
      return string.Empty;
    }
    var braceStart = source.IndexOf('{', fieldStart);
    var depth = 0;
    for (var i = braceStart; i < source.Length; i++) {
      if (source[i] == '{') {
        depth++;
      } else if (source[i] == '}' && --depth == 0) {
        return source[braceStart..(i + 1)];
      }
    }
    return source[braceStart..];
  }
}
