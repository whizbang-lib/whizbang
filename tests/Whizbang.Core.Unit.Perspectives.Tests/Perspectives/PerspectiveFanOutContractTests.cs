// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq.Expressions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The fan-out contract (#2618): a perspective declares the rows of its own model that another stream's event targets,
/// by a key column, and the set-based change the event makes to them.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/IPerspectiveFanOutFor.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/FanOutKey.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveFanOutEntry.cs</code-under-test>
public class PerspectiveFanOutContractTests {
  internal sealed class OrderSummary {
    [PhysicalField]
    [Indexed]
    public Guid CustomerId { get; init; }
    public string CustomerName { get; init; } = "";
  }

  internal sealed record CustomerRenamed(string Name) : IEvent;

  internal sealed record Spec(Expression<Action<ICollectiveSetters<OrderSummary>>> Setters) : ICollectiveSpec<OrderSummary>;

  internal sealed class OrderSummaryProjection : IPerspectiveFanOutFor<OrderSummary, CustomerRenamed> {
    public static FanOutKey<OrderSummary> Targets => FanOut.By<OrderSummary>(o => o.CustomerId);

    public ICollectiveSpec<OrderSummary> Set(CustomerRenamed @event) =>
      new Spec(s => s.SetProperty(o => o.CustomerName, @event.Name));
  }

  private static FanOutKey<OrderSummary> _targetsOf<TPerspective>()
      where TPerspective : IPerspectiveFanOutFor<OrderSummary, CustomerRenamed> =>
    TPerspective.Targets;

  [Test]
  public async Task FanOutBy_KeepsTheKeySelectorTheTargetsWereDeclaredWithAsync() {
    Expression<Func<OrderSummary, object?>> selector = o => o.CustomerId;

    var key = FanOut.By(selector);

    await Assert.That(key.Selector).IsSameReferenceAs(selector)
      .Because("the selector names the key column; the generator reads it at build time and the key keeps it as written");
  }

  [Test]
  public async Task Targets_IsReadThroughTheInterfaceAsync() {
    var key = _targetsOf<OrderSummaryProjection>();

    await Assert.That(key.Selector.Body.ToString()).Contains(nameof(OrderSummary.CustomerId))
      .Because("Targets is a static member of the interface, so a fan-out declares its key once per model and event");
  }

  [Test]
  public async Task FanOutBy_ANullSelector_ThrowsAsync() {
    await Assert.That(() => FanOut.By<OrderSummary>(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Entry_EntryKey_NamesThePerspectiveTheEventAndTheModelAsync() {
    var entry = _entry();

    await Assert.That(entry.EntryKey).IsEqualTo(
      "Whizbang.Core.Tests.Perspectives.PerspectiveFanOutContractTests+OrderSummaryProjection.Set("
      + "Whizbang.Core.Tests.Perspectives.PerspectiveFanOutContractTests+CustomerRenamed):"
      + "Whizbang.Core.Tests.Perspectives.PerspectiveFanOutContractTests+OrderSummary")
      .Because("one perspective has one Set per foreign event, so the event is part of what tells two fan-outs apart");
  }

  [Test]
  public async Task Entry_Invoker_CallsThePerspectivesSetForTheEventAsync() {
    var entry = _entry();

    var spec = entry.Invoker(new OrderSummaryProjection(), new CustomerRenamed("Ada"));

    await Assert.That(spec).IsTypeOf<Spec>();
    await Assert.That(spec.UntypedSetters.ToString()).Contains(nameof(OrderSummary.CustomerName));
  }

  private static PerspectiveFanOutEntry _entry() => new(
    PerspectiveType: typeof(OrderSummaryProjection),
    ModelType: typeof(OrderSummary),
    EventType: typeof(CustomerRenamed),
    KeyProperty: nameof(OrderSummary.CustomerId),
    KeyColumn: "customer_id",
    Invoker: static (perspective, evt) =>
      ((IPerspectiveFanOutFor<OrderSummary, CustomerRenamed>)perspective).Set((CustomerRenamed)evt));
}
