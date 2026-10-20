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
/// <para>The stage is detached (#1393): the dispatch does not wait for it. So each count is read once the receptor's
/// signal arrives, never when the dispatch returns. After #1386 one code path fires the stage, so nothing arrives
/// after the signal.</para>
/// <para>That the dispatch does not wait is shown by a receptor that cannot finish until the test releases it: the
/// dispatch returns while it is still blocked. Its exception never reaches the caller either.</para>
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
    await ImmediateDetachedProbe.Fired(id).Task;

    await Assert.That(ImmediateDetachedProbe.HandledCount(id)).IsEqualTo(1)
      .Because("the business receptor runs once");
    await Assert.That(ImmediateDetachedProbe.ImmediateDetachedCount(id)).IsEqualTo(1)
      .Because("ImmediateDetached fires once per message on the service that handles it, never again after a later stage");
  }

  public enum GatedEntry {
    SendGeneric,
    SendObject,
    LocalInvokeVoidGeneric,
    LocalInvokeVoidObject,
  }

  [Test]
  [MatrixDataSource]
  [Timeout(60_000)]
  public async Task ALocalDispatch_ReturnsWithoutWaitingForImmediateDetachedAsync(
      [Matrix] GatedEntry entry,
      [Matrix(false, true)] bool withLifecycleCoordinator,
      CancellationToken ct) {
    await using var provider = _buildProvider(withLifecycleCoordinator);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();

    // The receptor cannot finish until it is released, which happens only after the dispatch returns: a
    // dispatch that waits for it never returns, and the test fails on its timeout
    await _dispatchGatedAsync(dispatcher, entry, new GatedDetachedCommand(id)).WaitAsync(ct);
    await ImmediateDetachedProbe.Fired(id).Task.WaitAsync(ct);
    ImmediateDetachedProbe.Released(id).TrySetResult();

    await Assert.That(ImmediateDetachedProbe.ImmediateDetachedCount(id)).IsEqualTo(1)
      .Because("ImmediateDetached runs once, in the background, and the dispatch returned without waiting for it");
  }

  [Test]
  [MatrixDataSource]
  [Timeout(60_000)]
  public async Task AnImmediateDetachedFailure_NeverReachesTheCallerAsync(
      [Matrix] GatedEntry entry,
      [Matrix(false, true)] bool withLifecycleCoordinator,
      CancellationToken ct) {
    await using var provider = _buildProvider(withLifecycleCoordinator);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();

    await _dispatchGatedAsync(dispatcher, entry, new ThrowingDetachedCommand(id)).WaitAsync(ct);
    await ImmediateDetachedProbe.Fired(id).Task.WaitAsync(ct);

    await Assert.That(ImmediateDetachedProbe.HandledCount(id)).IsEqualTo(1)
      .Because("the dispatch completed: a detached stage's exception is logged, never returned to the caller");
  }

  private static async Task _dispatchGatedAsync<TCommand>(IDispatcher dispatcher, GatedEntry entry, TCommand command)
      where TCommand : notnull {
    switch (entry) {
      case GatedEntry.SendGeneric:
        await dispatcher.SendAsync(command);
        break;
      case GatedEntry.SendObject:
        await dispatcher.SendAsync((object)command);
        break;
      case GatedEntry.LocalInvokeVoidGeneric:
        await dispatcher.LocalInvokeAsync(command);
        break;
      case GatedEntry.LocalInvokeVoidObject:
        await dispatcher.LocalInvokeAsync((object)command);
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(entry), entry, null);
    }
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
