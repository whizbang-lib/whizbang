// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Data.EFCore.Postgres.Tests.DistributeStageSends;
using Whizbang.Data.EFCore.Postgres.Tests.DistributeStageSends.Owned;
using Whizbang.Data.EFCore.Postgres.Tests.DistributeStageSends.Upstream;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

#pragma warning disable CA1707 // Test method names use underscores by convention.

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// End to end for #1379 at the distribute stages: an event from a namespace the service does not own
/// is published, the REAL <see cref="ScopedWorkCoordinatorStrategy"/> flushes its row into a real
/// PostgreSQL outbox through <see cref="EFCoreWorkCoordinator{TDbContext}"/>, and a receptor firing at
/// a distribute stage of that flush sends a command the service owns, handled here by a receptor that
/// returns no result. The command must be handled exactly once, in-process, and never written to
/// wh_outbox. Before the fix SendAsync answered Accepted and dropped the command from every stage alike.
/// </summary>
/// <remarks>
/// <para>
/// The distribute stages fire only when the strategy is built with a scope factory and a lifecycle
/// message deserializer (see WorkCoordinatorFlushHelper and WorkCoordinatorStrategyFactory). The
/// sibling fixtures build the strategy without them, so this one supplies both through
/// <see cref="ScopedWorkCoordinatorDependencies"/>, the way the production factory does.
/// </para>
/// <para>
/// The owned command's handling does not depend on the flush under observation: it runs in-process
/// from the hook. Detached stages run on a fresh task and scope, so every assertion waits on the owned
/// command's own completion signal and on the hook's SendAsync returning, never on time. The trigger's
/// own outbox row is expected, and is asserted, so the absence of the command's row is a real reading
/// of the table rather than an empty query.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/WorkCoordinatorFlushHelper.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/ScopedWorkCoordinatorStrategy.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard5")]
public class DistributeStageOwnedCommandSendIntegrationTests : EFCoreTestBase {
  private const string OWNED_DOMAIN = "Whizbang.Data.EFCore.Postgres.Tests.DistributeStageSends.Owned";
  private static readonly TimeSpan _signalGuard = TimeSpan.FromSeconds(30);

  [Test]
  [Arguments(LifecycleStage.PreDistributeInline)]
  [Arguments(LifecycleStage.PreDistributeDetached)]
  [Arguments(LifecycleStage.DistributeDetached)]
  [Arguments(LifecycleStage.PostDistributeInline)]
  [Arguments(LifecycleStage.PostDistributeDetached)]
  public async Task ReceptorAtDistributeStage_SendsOwnedVoidCommand_HandledOnceAndNeverInTheOutboxAsync(LifecycleStage stage) {
    var serviceProvider = _createServices();
    var id = Guid.CreateVersion7();
    var handled = DistributeStageSendProbe.Handled(id);
    var sendReturned = DistributeStageSendProbe.SendReturned(id);
    var trigger = DistributeStageTriggers.For(stage, id);
    ScopeContextAccessor.CurrentInitiatingContext = null;
    ScopeContextAccessor.CurrentContext = null;

    try {
      var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();

      // The scoped strategy flushes inside PublishAsync: the trigger row is stored and the inline
      // stages have run when this returns; detached stages may still be running
      await _publishAsync(dispatcher, trigger);

      // The owned command's receptor completing is the transition under test; a send the dispatcher
      // dropped or threw on never completes it (the hook reports a throw as a fault)
      await handled.Task.WaitAsync(_signalGuard);
      var status = await sendReturned.Task.WaitAsync(_signalGuard);

      await Assert.That(DistributeStageSendProbe.CountFor(id)).IsEqualTo(1)
        .Because("the owned command must be handled exactly once, in-process");
      await Assert.That(status).IsEqualTo(DeliveryStatus.Delivered)
        .Because("before #1379 the send answered Accepted and nothing ran");

      await using var dbContext = CreateDbContext();
      var triggerName = trigger.GetType().FullName!;
      var commandName = typeof(DistributeStageOwnedCommand).FullName!;
      var triggerRows = await dbContext.Outbox.CountAsync(m => m.MessageType.Contains(triggerName));
      var commandRows = await dbContext.Outbox.CountAsync(m => m.MessageType.Contains(commandName));

      await Assert.That(triggerRows).IsEqualTo(1)
        .Because("the unowned trigger event is the row the flush stores and fires the stage for");
      await Assert.That(commandRows).IsEqualTo(0)
        .Because("a command the service owns and handles never goes to the outbox");
    } finally {
      ScopeContextAccessor.CurrentInitiatingContext = null;
      ScopeContextAccessor.CurrentContext = null;
      await serviceProvider.DisposeAsync();
    }
  }

  // Published under a tenant's system context, as a deployed service publishes: the distribute
  // stages establish the row's security context before invoking any receptor, and message security
  // refuses a row that carries none
  private static Task<IDeliveryReceipt> _publishAsync(IDispatcher dispatcher, object trigger) => trigger switch {
    PreDistributeInlineTrigger t => dispatcher.AsSystem().ForTenant("tenant-distribute-stage-sends").PublishAsync(t),
    PreDistributeDetachedTrigger t => dispatcher.AsSystem().ForTenant("tenant-distribute-stage-sends").PublishAsync(t),
    DistributeDetachedTrigger t => dispatcher.AsSystem().ForTenant("tenant-distribute-stage-sends").PublishAsync(t),
    PostDistributeInlineTrigger t => dispatcher.AsSystem().ForTenant("tenant-distribute-stage-sends").PublishAsync(t),
    PostDistributeDetachedTrigger t => dispatcher.AsSystem().ForTenant("tenant-distribute-stage-sends").PublishAsync(t),
    _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger.GetType().Name, "Unknown distribute-stage trigger")
  };

  private ServiceProvider _createServices() {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddScoped(_ => CreateDbContext());
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));

    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    services.AddSingleton(jsonOptions);
    services.AddSingleton<IEnvelopeSerializer, EnvelopeSerializer>();
    services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

    services.AddScoped<IWorkCoordinator>(sp =>
      new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
        sp.GetRequiredService<WorkCoordinationDbContext>(), jsonOptions));

    // Unlike the sibling fixtures, the strategy gets a scope factory and a lifecycle deserializer:
    // without both, the flush skips every distribute stage and this test would observe nothing
    services.AddScoped<IWorkCoordinatorStrategy>(sp =>
      new ScopedWorkCoordinatorStrategy(
        coordinator: sp.GetRequiredService<IWorkCoordinator>(),
        instanceProvider: sp.GetRequiredService<IServiceInstanceProvider>(),
        workChannelWriter: null,
        options: new WorkCoordinatorOptions { LeaseSeconds = 30, AbandonStaleInstanceThresholdSeconds = 300, PartitionCount = 4 },
        logger: sp.GetRequiredService<ILogger<ScopedWorkCoordinatorStrategy>>(),
        inboxChannelWriter: new InboxChannelWriter(),
        dependencies: new ScopedWorkCoordinatorDependencies {
          ScopeFactory = sp.GetRequiredService<IServiceScopeFactory>(),
          LifecycleMessageDeserializer = sp.GetRequiredService<ILifecycleMessageDeserializer>()
        }, detachedWork: new DetachedLifecycleWork()));

    services.AddReceptors();
    services.AddWhizbangDispatcher();
    services.AddWhizbangLifecycleMessageDeserializer();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();

    return services.BuildServiceProvider();
  }
}
