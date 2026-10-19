// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Component.Tests.LocalDetachedHooks;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// #1392, against the REAL generated dispatcher: the default local handler stage is LocalImmediateInline, because the
/// caller waits for its handler, and a receptor declared <c>[FireAt(LocalImmediateDetached)]</c> is a hook that runs
/// in the background after the handler. Before the fix the hook was awaited as a second handler, so the dispatch
/// waited for it and its exception reached the caller.
/// </summary>
/// <remarks>
/// That the dispatch does not wait is shown by a hook that cannot finish until the test releases it, which happens
/// only after the dispatch returns: a dispatch that waits never returns, and the test fails on its timeout.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/ReceptorDiscoveryGenerator.cs</code-under-test>
/// <docs>fundamentals/lifecycle/lifecycle-stages#localimmediatedetached</docs>
public class LocalImmediateDetachedHookComponentTests {
  public enum Entry {
    SendGeneric,
    SendObject,
    LocalInvokeVoidGeneric,
    LocalInvokeVoidObject,
  }

  [Test]
  [MatrixDataSource]
  [Timeout(60_000)]
  public async Task ALocalDispatch_ReturnsWithoutWaitingForALocalImmediateDetachedHookAsync([Matrix] Entry entry, CancellationToken ct) {
    await using var provider = _buildProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();

    await _dispatchAsync(dispatcher, entry, new LdGatedCommand(id)).WaitAsync(ct);
    await LocalDetachedProbe.HookStarted(id).Task.WaitAsync(ct);
    LocalDetachedProbe.Released(id).TrySetResult();

    await Assert.That(LocalDetachedProbe.HandledCount(id)).IsEqualTo(1)
      .Because("the handler runs once, and the caller waits for it");
    await Assert.That(LocalDetachedProbe.HookedCount(id)).IsEqualTo(1)
      .Because("the hook runs once, in the background, and the dispatch returned without waiting for it");
  }

  [Test]
  [MatrixDataSource]
  [Timeout(60_000)]
  public async Task ALocalImmediateDetachedHookFailure_NeverReachesTheCallerAsync([Matrix] Entry entry, CancellationToken ct) {
    await using var provider = _buildProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();

    await _dispatchAsync(dispatcher, entry, new LdThrowingCommand(id)).WaitAsync(ct);
    await LocalDetachedProbe.HookStarted(id).Task.WaitAsync(ct);

    await Assert.That(LocalDetachedProbe.HandledCount(id)).IsEqualTo(1)
      .Because("the dispatch completed: a detached hook's exception is logged, never returned to the caller");
  }

  [Test]
  [Timeout(60_000)]
  public async Task ALocalInvokeForAResult_ReturnsTheHandlersResult_NotTheHooksAsync(CancellationToken ct) {
    await using var provider = _buildProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();

    var result = await dispatcher.LocalInvokeAsync<LdResultCommand, LdResult>(new LdResultCommand(id)).AsTask().WaitAsync(ct);
    await LocalDetachedProbe.HookStarted(id).Task.WaitAsync(ct);

    await Assert.That(result.Id).IsEqualTo(id)
      .Because("the default handler is Inline: the caller waits for it and gets its result");
    await Assert.That(LocalDetachedProbe.HookedCount(id)).IsEqualTo(1);
  }

  private static async Task _dispatchAsync<TCommand>(IDispatcher dispatcher, Entry entry, TCommand command) where TCommand : notnull {
    switch (entry) {
      case Entry.SendGeneric:
        await dispatcher.SendAsync(command);
        break;
      case Entry.SendObject:
        await dispatcher.SendAsync((object)command);
        break;
      case Entry.LocalInvokeVoidGeneric:
        await dispatcher.LocalInvokeAsync(command);
        break;
      case Entry.LocalInvokeVoidObject:
        await dispatcher.LocalInvokeAsync((object)command);
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(entry), entry, null);
    }
  }

  private static ServiceProvider _buildProvider() {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }
}
