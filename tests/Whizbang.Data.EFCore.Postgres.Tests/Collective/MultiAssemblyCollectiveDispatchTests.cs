// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// A host whose collective apply handlers live in two assemblies dispatches each assembly's collective events to its
/// handlers (#2238).
/// </summary>
/// <remarks>
/// <para>
/// This assembly's handlers are the real generated table, registered by the generated module initializer that
/// <c>AddWhizbang()</c> replays or by the generated <c>AddCollectiveApplyRegistry</c> call. The second assembly is a
/// table registered through the same contribution call the generator emits. The executors record what they were asked
/// to apply, so the test needs no database: the question is which handlers the dispatcher knows.
/// </para>
/// <para>
/// Before the fix the dispatcher was given the one table a consumer passed by hand, so the other assembly's collective
/// events matched no handler and were never applied.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/CollectiveEventsEFCoreExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/CompositeCollectiveApplyRegistry.cs</code-under-test>
[Category("Shard1")]
public class MultiAssemblyCollectiveDispatchTests {
  private sealed class Ctx(DbContextOptions<Ctx> options) : DbContext(options);

  /// <summary>The second assembly's model, event and handler.</summary>
  private sealed class InvoiceModel;

  private sealed record VoidInvoicesCollectiveEvent : ICollectiveEvent {
    public CollectiveScope Scope { get; init; } = new TenantCollectiveScope("tenant-a");
  }

  private sealed class InvoiceHandler;

  private sealed class SecondAssemblyRegistry : ICollectiveApplyRegistry {
    public IReadOnlyList<CollectiveApplyEntry> Entries { get; } = [
      new CollectiveApplyEntry(typeof(InvoiceModel), typeof(VoidInvoicesCollectiveEvent), typeof(InvoiceHandler), "Void",
        CollectiveScopeHandling.Framework, CollectiveSpecKind.Linq, Invoker: static (_, _, _) => new object()),
    ];
  }

  /// <summary>Records each apply it is asked for: the handler's method and the event.</summary>
  private sealed class RecordingExecutor<TModel>(List<string> applied) : ICollectiveEventExecutor {
    public Type ModelType => typeof(TModel);

    public Task<int> ApplyAsync(CollectiveApplyEntry entry, object handlerInstance, ICollectiveEvent evt,
        ICollectiveScopeResolver resolver, object dbContextOrSession, Guid collectiveEventId,
        Func<CancellationToken, ValueTask>? onBatchApplied = null, CancellationToken cancellationToken = default) {
      applied.Add($"{entry.MethodName}:{evt.GetType().Name}");
      return Task.FromResult(1);
    }
  }

  private static void _registerHandlersAndExecutors(IServiceCollection services, List<string> applied) {
    services.AddSingleton<RebuildBonusCollectiveHandler>();
    services.AddSingleton<InvoiceHandler>();
    services.AddSingleton<ICollectiveEventExecutor>(new RecordingExecutor<RebuildBalanceModel>(applied));
    services.AddSingleton<ICollectiveEventExecutor>(new RecordingExecutor<InvoiceModel>(applied));
  }

  private static void _registerSecondAssembly(IServiceCollection services) {
    services.AddSingleton<SecondAssemblyRegistry>();
    services.AddCollectiveApplyRegistryContribution<SecondAssemblyRegistry>();
  }

  private static async Task<List<string>> _dispatchBothAsync(IServiceProvider provider) {
    var dispatcher = provider.GetRequiredService<ICollectiveDispatcher>();
    var thisAssemblys = await dispatcher.DispatchAsync(
      new RebuildBonusCollectiveEvent { Amount = 1m, Scope = new TenantCollectiveScope("tenant-a") }, Guid.NewGuid(), new object());
    var secondAssemblys = await dispatcher.DispatchAsync(new VoidInvoicesCollectiveEvent(), Guid.NewGuid(), new object());
    return [$"{thisAssemblys.HandlerCount}", $"{secondAssemblys.HandlerCount}"];
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task TwoAssemblies_TheDispatcher_AppliesEachAssemblysCollectiveEventsAsync(bool thisAssemblyFirst) {
    var applied = new List<string>();
    var services = new ServiceCollection();
    if (thisAssemblyFirst) {
      Generated.CollectiveApplyRegistryExtensions.AddCollectiveApplyRegistry(services);
      _registerSecondAssembly(services);
    } else {
      _registerSecondAssembly(services);
      Generated.CollectiveApplyRegistryExtensions.AddCollectiveApplyRegistry(services);
    }
    services.AddCollectiveEventsEFCore<Ctx>();
    _registerHandlersAndExecutors(services, applied);
    await using var provider = services.BuildServiceProvider();

    var handlerCounts = await _dispatchBothAsync(provider);

    await Assert.That(handlerCounts).IsEquivalentTo(["1", "1"])
      .Because("each event matches the one handler its own assembly declares");
    await Assert.That(applied).IsEquivalentTo(["Bonus:RebuildBonusCollectiveEvent", "Void:VoidInvoicesCollectiveEvent"]);
  }

  [Test]
  public async Task AddWhizbang_GivesTheDispatcherThisAssemblysHandlers_WithNothingPassedByHandAsync() {
    var applied = new List<string>();
    var services = new ServiceCollection();
    services.AddWhizbang();
    _registerSecondAssembly(services);
    services.AddCollectiveEventsEFCore<Ctx>();
    _registerHandlersAndExecutors(services, applied);
    await using var provider = services.BuildServiceProvider();

    var handlerCounts = await _dispatchBothAsync(provider);

    await Assert.That(handlerCounts).IsEquivalentTo(["1", "1"])
      .Because("the generated module initializer registers this assembly's table; AddWhizbang replays it");
    await Assert.That(applied).Contains("Bonus:RebuildBonusCollectiveEvent");
  }

  [Test]
  public async Task TheObsoleteHandPassedTable_StillApplies_AlongsideTheContributedOnesAsync() {
    var applied = new List<string>();
    var services = new ServiceCollection();
    Generated.CollectiveApplyRegistryExtensions.AddCollectiveApplyRegistry(services);
#pragma warning disable CS0618 // the hand-passed overload is obsolete; this pins that it keeps working
    services.AddCollectiveEventsEFCore<Ctx>(new SecondAssemblyRegistry().Entries);
#pragma warning restore CS0618
    _registerHandlersAndExecutors(services, applied);
    await using var provider = services.BuildServiceProvider();

    var handlerCounts = await _dispatchBothAsync(provider);

    await Assert.That(handlerCounts).IsEquivalentTo(["1", "1"])
      .Because("a hand-passed table joins the contributed ones instead of replacing them");
  }

  [Test]
  public async Task AHandPassedTableTheHostAlsoContributes_DispatchesEachHandlerOnceAsync() {
    var applied = new List<string>();
    var services = new ServiceCollection();
    Generated.CollectiveApplyRegistryExtensions.AddCollectiveApplyRegistry(services);
#pragma warning disable CS0618 // the hand-passed overload is obsolete; this pins that it keeps working
    services.AddCollectiveEventsEFCore<Ctx>(new Generated.GeneratedCollectiveApplyRegistry().Entries);
#pragma warning restore CS0618
    _registerHandlersAndExecutors(services, applied);
    await using var provider = services.BuildServiceProvider();

    var dispatched = await provider.GetRequiredService<ICollectiveDispatcher>().DispatchAsync(
      new RebuildBonusCollectiveEvent { Amount = 1m, Scope = new TenantCollectiveScope("tenant-a") }, Guid.NewGuid(), new object());

    await Assert.That(dispatched.HandlerCount).IsEqualTo(1)
      .Because("the table a consumer passed is the one its assembly already contributes; a handler applies once");
  }
}
