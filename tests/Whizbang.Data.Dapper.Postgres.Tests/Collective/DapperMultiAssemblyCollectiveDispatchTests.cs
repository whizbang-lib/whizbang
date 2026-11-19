// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// The Dapper collective registration gives the dispatcher every assembly's collective apply handlers (#2238), the
/// same as the EF Core one.
/// </summary>
/// <remarks>
/// Two stand-in assemblies' tables are registered through the contribution call the generator emits, and the
/// executors record what they are asked to apply, so no database is needed.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/CollectiveEventsDapperExtensions.cs</code-under-test>
public class DapperMultiAssemblyCollectiveDispatchTests {
  private sealed class OrderModel;
  private sealed class InvoiceModel;

  private sealed record RepriceOrders : ICollectiveEvent {
    public CollectiveScope Scope { get; init; } = new TenantCollectiveScope("tenant-a");
  }

  private sealed record VoidInvoices : ICollectiveEvent {
    public CollectiveScope Scope { get; init; } = new TenantCollectiveScope("tenant-a");
  }

  private sealed class OrderHandler;
  private sealed class InvoiceHandler;

  private static CollectiveApplyEntry _entry(Type model, Type evt, Type handler) =>
    new(model, evt, handler, "Apply", CollectiveScopeHandling.Framework, CollectiveSpecKind.Linq,
      Invoker: static (_, _, _) => new object());

  private sealed class OrdersAssembly : ICollectiveApplyRegistry {
    public IReadOnlyList<CollectiveApplyEntry> Entries { get; } = [_entry(typeof(OrderModel), typeof(RepriceOrders), typeof(OrderHandler))];
  }

  private sealed class InvoicesAssembly : ICollectiveApplyRegistry {
    public IReadOnlyList<CollectiveApplyEntry> Entries { get; } = [_entry(typeof(InvoiceModel), typeof(VoidInvoices), typeof(InvoiceHandler))];
  }

  private sealed class RecordingExecutor<TModel>(List<string> applied) : ICollectiveEventExecutor {
    public Type ModelType => typeof(TModel);

    public Task<int> ApplyAsync(CollectiveApplyEntry entry, object handlerInstance, ICollectiveEvent evt,
        ICollectiveScopeResolver resolver, object dbContextOrSession, Guid collectiveEventId,
        Func<CancellationToken, ValueTask>? onBatchApplied = null, CancellationToken cancellationToken = default) {
      applied.Add(evt.GetType().Name);
      return Task.FromResult(1);
    }
  }

  private static List<string> _registerExecutors(IServiceCollection services) {
    var applied = new List<string>();
    services.AddSingleton<OrderHandler>();
    services.AddSingleton<InvoiceHandler>();
    services.AddSingleton<ICollectiveEventExecutor>(new RecordingExecutor<OrderModel>(applied));
    services.AddSingleton<ICollectiveEventExecutor>(new RecordingExecutor<InvoiceModel>(applied));
    return applied;
  }

  private static async Task _dispatchBothAsync(IServiceProvider provider) {
    var dispatcher = provider.GetRequiredService<ICollectiveDispatcher>();
    _ = await dispatcher.DispatchAsync(new RepriceOrders(), Guid.NewGuid(), new object());
    _ = await dispatcher.DispatchAsync(new VoidInvoices(), Guid.NewGuid(), new object());
  }

  [Test]
  public async Task TwoAssemblies_TheDispatcher_AppliesEachAssemblysCollectiveEventsAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<OrdersAssembly>();
    services.AddCollectiveApplyRegistryContribution<OrdersAssembly>();
    services.AddSingleton<InvoicesAssembly>();
    services.AddCollectiveApplyRegistryContribution<InvoicesAssembly>();
    services.AddCollectiveEventsDapper();
    var applied = _registerExecutors(services);
    await using var provider = services.BuildServiceProvider();

    await _dispatchBothAsync(provider);

    await Assert.That(applied).IsEquivalentTo([nameof(RepriceOrders), nameof(VoidInvoices)]);
  }

  [Test]
  public async Task TheObsoleteHandPassedTable_StillApplies_AlongsideTheContributedOnesAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<OrdersAssembly>();
    services.AddCollectiveApplyRegistryContribution<OrdersAssembly>();
#pragma warning disable CS0618 // the hand-passed overload is obsolete; this pins that it keeps working
    services.AddCollectiveEventsDapper(new InvoicesAssembly().Entries);
#pragma warning restore CS0618
    var applied = _registerExecutors(services);
    await using var provider = services.BuildServiceProvider();

    await _dispatchBothAsync(provider);

    await Assert.That(applied).IsEquivalentTo([nameof(RepriceOrders), nameof(VoidInvoices)])
      .Because("a hand-passed table joins the contributed ones instead of replacing them");
  }
}
