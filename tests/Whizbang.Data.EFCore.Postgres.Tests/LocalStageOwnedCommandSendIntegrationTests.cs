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
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.EFCore.Postgres.Tests.LocalStageSends;
using Whizbang.Data.EFCore.Postgres.Tests.LocalStageSends.Owned;

#pragma warning disable CA1707 // Test method names use underscores by convention.

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// End to end for #1379 on the LOCAL dispatch path, against a real PostgreSQL outbox: a receptor
/// that fires at a local lifecycle stage sends a command the service owns, handled here by a
/// receptor that returns no result. The command must be handled exactly once, in-process, and never
/// written to wh_outbox. Before the fix SendAsync answered Accepted and dropped such a command from
/// every stage alike; the fixtures that hid it ran without owned domains, which this one configures
/// the way a deployed service does.
/// </summary>
/// <remarks>
/// <para>
/// ImmediateDetached fires after SendAsync's business receptor completes. PostLifecycleInline and
/// PostLifecycleDetached fire only from LocalInvokeAsync; with an <see cref="ILifecycleCoordinator"/>
/// registered the detached stage runs on its own task and scope, and without one it is awaited
/// inline, so both configurations are covered. LocalImmediateInline and LocalImmediateDetached are
/// not fired as lifecycle stages: a receptor declared at one is the message's handler for local
/// dispatch, so for those the trigger's own handler makes the send.
/// </para>
/// <para>
/// The outbox is the real one: <see cref="ScopedWorkCoordinatorStrategy"/> over
/// <c>EFCoreWorkCoordinator</c>. Every assertion waits on the owned command's own completion signal
/// and on the hook's send returning, never on time.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Lifecycle/LifecycleCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Lifecycle/LifecycleTrackingState.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard5")]
public class LocalStageOwnedCommandSendIntegrationTests : EFCoreTestBase {
  private const string OWNED_DOMAIN = "Whizbang.Data.EFCore.Postgres.Tests.LocalStageSends.Owned";

  public enum TriggerEntry { Send, LocalInvoke }

  [Test]
  [Arguments(LifecycleStage.ImmediateDetached, TriggerEntry.Send, false)]
  [Arguments(LifecycleStage.ImmediateDetached, TriggerEntry.Send, true)]
  [Arguments(LifecycleStage.PostLifecycleInline, TriggerEntry.LocalInvoke, false)]
  [Arguments(LifecycleStage.PostLifecycleInline, TriggerEntry.LocalInvoke, true)]
  [Arguments(LifecycleStage.PostLifecycleDetached, TriggerEntry.LocalInvoke, false)]
  [Arguments(LifecycleStage.PostLifecycleDetached, TriggerEntry.LocalInvoke, true)]
  [Arguments(LifecycleStage.LocalImmediateInline, TriggerEntry.Send, false)]
  [Arguments(LifecycleStage.LocalImmediateInline, TriggerEntry.LocalInvoke, false)]
  [Arguments(LifecycleStage.LocalImmediateDetached, TriggerEntry.Send, false)]
  [Arguments(LifecycleStage.LocalImmediateDetached, TriggerEntry.LocalInvoke, false)]
  public async Task ReceptorAtLocalStage_SendsOwnedVoidCommand_HandledOnceAndNeverInTheOutboxAsync(
      LifecycleStage stage, TriggerEntry entry, bool withLifecycleCoordinator) {
    var serviceProvider = await _createServicesAsync(withLifecycleCoordinator);
    var trigger = LocalStageTriggers.For(stage);
    var id = LocalStageTriggers.IdOf(trigger);
    var handled = LocalStageSendProbe.Handled(id);
    var sendReturned = LocalStageSendProbe.SendReturned(id);
    ScopeContextAccessor.CurrentInitiatingContext = null;
    ScopeContextAccessor.CurrentContext = null;

    try {
      var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();
      if (entry == TriggerEntry.Send) {
        await dispatcher.SendAsync(trigger);
      } else {
        await dispatcher.LocalInvokeAsync(trigger);
      }

      // The owned command's receptor completing, and the hook's send returning, are the
      // transitions under test; a send the dispatcher dropped never completes them and a throw
      // faults them
      await handled.Task.WaitAsync(TimeSpan.FromSeconds(30));
      await sendReturned.Task.WaitAsync(TimeSpan.FromSeconds(30));

      await Assert.That(LocalStageSendProbe.CountFor(id)).IsEqualTo(1)
        .Because("the owned command must be handled exactly once, in-process");

      await using var dbContext = CreateDbContext();
      var commandType = TypeNameFormatter.AssemblyQualifiedName(typeof(OwnedLocalStageCommand));
      var outboxCopies = await dbContext.Outbox.CountAsync(m => m.MessageType == commandType);
      await Assert.That(outboxCopies).IsEqualTo(0)
        .Because("a command the service owns and handles never goes to the outbox");
    } finally {
      ScopeContextAccessor.CurrentInitiatingContext = null;
      ScopeContextAccessor.CurrentContext = null;
      await serviceProvider.DisposeAsync();
    }
  }

  private async Task<ServiceProvider> _createServicesAsync(bool withLifecycleCoordinator) {
    await base.SetupAsync();

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

    services.AddScoped<IWorkCoordinatorStrategy>(sp =>
      new ScopedWorkCoordinatorStrategy(
        coordinator: sp.GetRequiredService<IWorkCoordinator>(),
        instanceProvider: sp.GetRequiredService<IServiceInstanceProvider>(),
        workChannelWriter: null,
        options: new WorkCoordinatorOptions { LeaseSeconds = 30, AbandonStaleInstanceThresholdSeconds = 300, PartitionCount = 4 },
        logger: sp.GetRequiredService<ILogger<ScopedWorkCoordinatorStrategy>>(),
        inboxChannelWriter: new InboxChannelWriter()));

    if (withLifecycleCoordinator) {
      services.AddSingleton<ILifecycleCoordinator, LifecycleCoordinator>();
    }

    services.AddReceptors();
    services.AddWhizbangDispatcher();
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();

    return services.BuildServiceProvider();
  }
}
