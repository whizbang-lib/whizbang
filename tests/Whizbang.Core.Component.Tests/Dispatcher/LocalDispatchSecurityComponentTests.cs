// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Component.Tests.LocalDispatchSecurity;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Exceptions;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// #1419, against the REAL generated dispatcher: with message security required
/// (<see cref="MessageSecurityOptions.AllowAnonymous"/> = false) and no caller context, whether a local
/// dispatch was refused depended on the handler's shape, and the refusal came from a lifecycle stage AFTER
/// the handler had run. Now an in-process dispatch runs by default, like a mediator call, and
/// <see cref="MessageSecurityOptions.RequireContextForLocalDispatch"/> refuses it up front instead.
/// </summary>
/// <remarks>
/// Every message here has a PostLifecycleInline receptor, so every local run reaches a stage after its
/// handler, which is where the old refusal fired.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/ReceptorInvoker.cs</code-under-test>
/// <docs>fundamentals/security/message-security#local-dispatch</docs>
[NotInParallel("LocalDispatchSecurity")]
public class LocalDispatchSecurityComponentTests {
  public enum Entry {
    SendAsync,
    SendManyAsync,
    PublishAsync,
    PublishManyAsync,
    CascadeLocal,
    LocalInvokeVoid,
    LocalInvokeResult,
  }

  [Before(Test)]
  public void ClearTheCallerContext() {
    ScopeContextAccessor.CurrentContext = null;
    ScopeContextAccessor.CurrentInitiatingContext = null;
  }

  [Test]
  [MatrixDataSource]
  public async Task EveryLocalEntry_WithoutACallerContext_RunsByDefaultAsync([Matrix] Entry entry) {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox, requireContext: false);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();

    await _dispatchAsync(dispatcher, entry, id);

    await Assert.That(LocalSecurityProbe.HandledCount(id)).IsEqualTo(1)
      .Because("an in-process dispatch runs without a caller context by default");
    await Assert.That(LocalSecurityProbe.PostLifecycleCount(id)).IsEqualTo(1)
      .Because("the stage after the handler does not refuse it either: a refusal after the handler ran protects nothing");
  }

  [Test]
  [MatrixDataSource]
  public async Task EveryLocalEntry_WithoutACallerContext_IsRefusedUpFrontWhenRequiredAsync([Matrix] Entry entry) {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox, requireContext: true);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();

    await Assert.That(async () => await _dispatchAsync(dispatcher, entry, id)).Throws<SecurityContextRequiredException>()
      .Because("RequireContextForLocalDispatch refuses a dispatch whose caller has no context");
    await Assert.That(LocalSecurityProbe.HandledCount(id)).IsEqualTo(0)
      .Because("the refusal comes before any handler runs");
    await Assert.That(outbox.QueuedOf(typeof(SlEvent)).Count + outbox.QueuedOf(typeof(SlCommand)).Count).IsEqualTo(0)
      .Because("the refusal comes before anything is written");
  }

  [Test]
  [Arguments(Entry.SendAsync)]
  [Arguments(Entry.PublishAsync)]
  [Arguments(Entry.LocalInvokeVoid)]
  [Arguments(Entry.LocalInvokeResult)]
  public async Task EveryLocalEntry_WithACallerContext_RunsWhenRequiredAsync(Entry entry) {
    await using var provider = _buildProvider(new PolicyRecordingOutbox(), requireContext: true);
    var asTenant = provider.GetRequiredService<IDispatcher>().AsSystem().ForTenant("tenant-local-security");
    var id = Guid.CreateVersion7();

    switch (entry) {
      case Entry.SendAsync: await asTenant.SendAsync(new SlCommand(id)); break;
      case Entry.PublishAsync: await asTenant.PublishAsync(new SlEvent(id)); break;
      case Entry.LocalInvokeVoid: await asTenant.LocalInvokeAsync(new SlCommand(id)); break;
      default: await asTenant.LocalInvokeAsync<SlResultCommand, SlResult>(new SlResultCommand(id)); break;
    }

    await Assert.That(LocalSecurityProbe.HandledCount(id)).IsEqualTo(1)
      .Because("a caller with a context is never refused");
  }

  private static async Task _dispatchAsync(IDispatcher dispatcher, Entry entry, Guid id) {
    switch (entry) {
      case Entry.SendAsync:
        await dispatcher.SendAsync(new SlCommand(id));
        break;
      case Entry.SendManyAsync:
        await dispatcher.SendManyAsync<SlCommand>([new SlCommand(id)]);
        break;
      case Entry.PublishAsync:
        await dispatcher.PublishAsync(new SlEvent(id));
        break;
      case Entry.PublishManyAsync:
        await dispatcher.PublishManyAsync<SlEvent>([new SlEvent(id)]);
        break;
      case Entry.CascadeLocal:
        await dispatcher.CascadeMessageAsync(new SlEvent(id), sourceEnvelope: null, DispatchModes.Local);
        break;
      case Entry.LocalInvokeVoid:
        await dispatcher.LocalInvokeAsync(new SlCommand(id));
        break;
      case Entry.LocalInvokeResult:
        await dispatcher.LocalInvokeAsync<SlResultCommand, SlResult>(new SlResultCommand(id));
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(entry), entry, null);
    }
  }

  private static ServiceProvider _buildProvider(PolicyRecordingOutbox outbox, bool requireContext) {
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(options => options.RequireContextForLocalDispatch = requireContext);
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddScoped<IWorkCoordinatorStrategy>(_ => outbox);
    services.AddSingleton<IEnvelopeSerializer>(new PolicyContextKeepingEnvelopeSerializer());
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }
}
