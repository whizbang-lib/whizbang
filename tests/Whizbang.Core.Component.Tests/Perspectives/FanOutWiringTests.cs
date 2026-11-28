// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// #2620: a declared fan-out cannot be left unwired. The collective dispatcher creates the executor of each fan-out model
/// that has none, through the driver's <see cref="ICollectiveExecutorFactory"/> and the entry's generated
/// <see cref="PerspectiveFanOutEntry.CreateExecutor"/>; and a host that declares a fan-out with no collective dispatcher,
/// or with a model no executor can be made for, is refused at startup, naming the model and the line to add.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/CollectiveExecutors.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveFanOutWiringValidator.cs</code-under-test>
/// <docs>fundamentals/perspectives/multi-stream#fan-out-design-choices</docs>
[Category("Unit")]
[Category("CollectiveEvents")]
public class FanOutWiringTests {
  [Test]
  public async Task ForDispatch_CreatesOneExecutorPerFanOutModelThatHasNone_AndKeepsTheRegisteredOnesAsync() {
    var registered = new ModelExecutor(typeof(InvoiceSummary));
    var factory = new RecordingFactory();

    var executors = CollectiveExecutors.ForDispatch(
      [registered], [_fanOut<OrderSummary>("wh_per_order_summary"), _fanOut<OrderSummary>("wh_per_order_summary"), _fanOut<InvoiceSummary>("wh_per_invoice_summary")],
      factory);

    await Assert.That(executors.Select(e => e.ModelType)).IsEquivalentTo([typeof(InvoiceSummary), typeof(OrderSummary)]);
    await Assert.That(executors[0]).IsSameReferenceAs(registered).Because("a model's own registration wins");
    await Assert.That(factory.Created).IsEquivalentTo([(typeof(OrderSummary), "wh_per_order_summary")])
      .Because("one executor per model, made with the model's table, however many fan-outs target it");
  }

  [Test]
  public async Task ForDispatch_AFanOutWithNoFactoryLambda_GetsNoExecutorAsync() {
    var executors = CollectiveExecutors.ForDispatch([], [_handMade()], new RecordingFactory());

    await Assert.That(executors).IsEmpty().Because("the startup validator refuses this host, naming the line to add");
  }

  [Test]
  public async Task Validator_AHostWithNoFanOut_StartsAsync() {
    await using var services = new ServiceCollection().BuildServiceProvider();

    await new PerspectiveFanOutWiringValidator(services).StartAsync(CancellationToken.None);
  }

  [Test]
  public async Task Validator_AFanOutWithItsDispatcher_StartsAsync() {
    await using var services = _services([_fanOut<OrderSummary>("wh_per_order_summary")], withDispatcher: true);

    await new PerspectiveFanOutWiringValidator(services).StartAsync(CancellationToken.None);
  }

  [Test]
  public async Task Validator_AFanOutWithNoCollectiveDispatcher_RefusesTheHost_NamingTheModelAndTheLineAsync() {
    await using var services = _services([_fanOut<OrderSummary>("wh_per_order_summary")], withDispatcher: false);

    var refused = await Assert.That(() => new PerspectiveFanOutWiringValidator(services).StartAsync(CancellationToken.None))
      .Throws<InvalidOperationException>();

    await Assert.That(refused!.Message).Contains(nameof(OrderSummary));
    await Assert.That(refused.Message).Contains("AddCollectiveEventsEFCore<");
    await Assert.That(refused.Message).Contains("AddCollectiveEventsDapper()");
  }

  [Test]
  public async Task Validator_AFanOutModelNoExecutorCanBeMadeFor_RefusesTheHost_NamingTheLineAsync() {
    await using var services = _services([_handMade()], withDispatcher: true);

    var refused = await Assert.That(() => new PerspectiveFanOutWiringValidator(services).StartAsync(CancellationToken.None))
      .Throws<InvalidOperationException>();

    await Assert.That(refused!.Message).Contains($"AddCollectiveExecutorEFCore<{nameof(OrderSummary)}>()");
  }

  [Test]
  public async Task Validator_AHandMadeFanOutWhoseModelHasARegisteredExecutor_StartsAsync() {
    await using var services = _services([_handMade()], withDispatcher: true, executorFor: typeof(OrderSummary));

    await new PerspectiveFanOutWiringValidator(services).StartAsync(CancellationToken.None);
    await new PerspectiveFanOutWiringValidator(services).StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task AddPerspectiveFanOutRegistryContribution_RegistersTheValidator_OnceAsync() {
    var services = new ServiceCollection().AddLogging();
    services.AddSingleton<FanOutsA>().AddPerspectiveFanOutRegistryContribution<FanOutsA>();
    services.AddSingleton<FanOutsB>().AddPerspectiveFanOutRegistryContribution<FanOutsB>();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetServices<IHostedService>().Count(s => s is PerspectiveFanOutWiringValidator)).IsEqualTo(1)
      .Because("every host that declares a fan-out is checked, whatever registered it");
  }

  // ---------- fixtures ----------

  private static ServiceProvider _services(
      IReadOnlyList<PerspectiveFanOutEntry> fanOuts, bool withDispatcher, Type? executorFor = null) {
    var services = new ServiceCollection().AddSingleton<IPerspectiveFanOutRegistry>(new Listed(fanOuts));
    if (withDispatcher) {
      services.AddSingleton<ICollectiveDispatcher>(new NoDispatcher());
    }
    if (executorFor is not null) {
      services.AddSingleton<ICollectiveEventExecutor>(new ModelExecutor(executorFor));
    }
    return services.BuildServiceProvider();
  }

  private static PerspectiveFanOutEntry _fanOut<TModel>(string table) where TModel : class => new(
    typeof(object), typeof(TModel), typeof(Renamed), "Key", "key", static (_, _) => null!,
    (Expression<Func<TModel, object?>>)(row => row), table, factory => factory.Create<TModel>(table));

  private static PerspectiveFanOutEntry _handMade() => new(
    typeof(object), typeof(OrderSummary), typeof(Renamed), "Key", "key", static (_, _) => null!,
    (Expression<Func<OrderSummary, object?>>)(row => row));

  internal sealed class OrderSummary;

  internal sealed class InvoiceSummary;

  internal sealed record Renamed : IEvent;

  private sealed class Listed(IReadOnlyList<PerspectiveFanOutEntry> entries) : IPerspectiveFanOutRegistry {
    public IReadOnlyList<PerspectiveFanOutEntry> Entries => entries;
  }

  private sealed class FanOutsA : IPerspectiveFanOutRegistry {
    public IReadOnlyList<PerspectiveFanOutEntry> Entries => [_fanOut<OrderSummary>("wh_per_order_summary")];
  }

  private sealed class FanOutsB : IPerspectiveFanOutRegistry {
    public IReadOnlyList<PerspectiveFanOutEntry> Entries => [_fanOut<InvoiceSummary>("wh_per_invoice_summary")];
  }

  private sealed class RecordingFactory : ICollectiveExecutorFactory {
    public List<(Type Model, string Table)> Created { get; } = [];

    public ICollectiveEventExecutor Create<TModel>(string tableName) where TModel : class {
      Created.Add((typeof(TModel), tableName));
      return new ModelExecutor(typeof(TModel));
    }
  }

  private sealed class ModelExecutor(Type model) : ICollectiveEventExecutor {
    public Type ModelType => model;

    public Task<int> ApplyAsync(
        CollectiveApplyEntry entry, object handlerInstance, ICollectiveEvent evt, ICollectiveScopeResolver resolver,
        object dbContextOrSession, Guid collectiveEventId, Func<CancellationToken, ValueTask>? onBatchApplied = null,
        CancellationToken cancellationToken = default) => Task.FromResult(0);
  }

  private sealed class NoDispatcher : ICollectiveDispatcher {
    public Task<CollectiveDispatchResult> DispatchAsync(
        ICollectiveEvent evt, Guid collectiveEventId, object dbContextOrSession,
        Func<CancellationToken, ValueTask>? onBatchApplied = null, CancellationToken cancellationToken = default) =>
      Task.FromResult(new CollectiveDispatchResult(0, 0));
  }
}
