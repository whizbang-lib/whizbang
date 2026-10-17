// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Component.Tests.ImmediateDetachedCounts;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// #1386, against the REAL generated dispatcher and receptor registry: a receptor at
/// <see cref="LifecycleStage.ImmediateDetached"/> runs once per message on the service that handles
/// it, right after the business receptor. Before the fix one <c>LocalInvokeAsync</c> reached it three
/// times without a lifecycle coordinator (after the receptor, then after each PostLifecycle stage)
/// and at least twice with one.
/// </summary>
/// <remarks>
/// Every count is read after the dispatch returns. The dispatcher awaits its ImmediateDetached call,
/// so the one expected invocation has happened by then. Before the fix the extra invocations after
/// <see cref="LifecycleStage.PostLifecycleInline"/> were awaited too, so the old behavior fails here
/// deterministically; after it, no code path fires the stage again, so nothing can arrive late.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Lifecycle/LifecycleTrackingState.cs</code-under-test>
/// <docs>fundamentals/lifecycle/lifecycle-stages#immediatedetached</docs>
public class ImmediateDetachedOncePerDispatchComponentTests {
  public enum Entry {
    SendGeneric,
    SendObject,
    SendWithOptions,
    LocalInvokeVoidGeneric,
    LocalInvokeVoidObject,
    LocalInvokeVoidWithOptions,
    LocalInvokeTypedGeneric,
    LocalInvokeTypedObject,
    LocalInvokeTypedWithOptions,
    LocalInvokeWithReceiptGeneric,
    LocalInvokeWithReceiptObject,
  }

  [Test]
  [MatrixDataSource]
  public async Task OneLocalDispatch_FiresImmediateDetachedExactlyOnceAsync(
      [Matrix] Entry entry,
      [Matrix(false, true)] bool withLifecycleCoordinator) {
    await using var provider = _buildProvider(withLifecycleCoordinator);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();

    await _dispatchAsync(dispatcher, entry, id);

    await Assert.That(ImmediateDetachedProbe.HandledCount(id)).IsEqualTo(1)
      .Because("the business receptor runs once");
    await Assert.That(ImmediateDetachedProbe.ImmediateDetachedCount(id)).IsEqualTo(1)
      .Because("ImmediateDetached fires once per message on the service that handles it, never again after a later stage");
  }

  private static async Task _dispatchAsync(IDispatcher dispatcher, Entry entry, Guid id) {
    var voidCommand = new CountedVoidCommand(id);
    var resultCommand = new CountedResultCommand(id);
    switch (entry) {
      case Entry.SendGeneric:
        await dispatcher.SendAsync(voidCommand);
        break;
      case Entry.SendObject:
        await dispatcher.SendAsync((object)voidCommand);
        break;
      case Entry.SendWithOptions:
        await dispatcher.SendAsync((object)voidCommand, new DispatchOptions());
        break;
      case Entry.LocalInvokeVoidGeneric:
        await dispatcher.LocalInvokeAsync(voidCommand);
        break;
      case Entry.LocalInvokeVoidObject:
        await dispatcher.LocalInvokeAsync((object)voidCommand);
        break;
      case Entry.LocalInvokeVoidWithOptions:
        await dispatcher.LocalInvokeAsync((object)voidCommand, new DispatchOptions());
        break;
      case Entry.LocalInvokeTypedGeneric:
        await dispatcher.LocalInvokeAsync<CountedResultCommand, CountedResult>(resultCommand);
        break;
      case Entry.LocalInvokeTypedObject:
        await dispatcher.LocalInvokeAsync<CountedResult>((object)resultCommand);
        break;
      case Entry.LocalInvokeTypedWithOptions:
        await dispatcher.LocalInvokeAsync<CountedResult>((object)resultCommand, new DispatchOptions());
        break;
      case Entry.LocalInvokeWithReceiptGeneric:
        await dispatcher.LocalInvokeWithReceiptAsync<CountedResultCommand, CountedResult>(resultCommand);
        break;
      case Entry.LocalInvokeWithReceiptObject:
        await dispatcher.LocalInvokeWithReceiptAsync<CountedResult>((object)resultCommand);
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(entry), entry, null);
    }
  }

  private static ServiceProvider _buildProvider(bool withLifecycleCoordinator) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    if (withLifecycleCoordinator) {
      services.AddSingleton<ILifecycleCoordinator>(new LifecycleCoordinator(NullLogger<LifecycleCoordinator>.Instance));
    }
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }
}
