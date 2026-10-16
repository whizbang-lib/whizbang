// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.EFCore.Postgres.Tests.StageSends;
using Whizbang.Data.EFCore.Postgres.Tests.StageSends.Owned;
using Whizbang.Data.EFCore.Postgres.Tests.StageSends.Upstream;
using Whizbang.Testing.Workers;

#pragma warning disable CA1707 // Test method names use underscores by convention.

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// End to end for #1379: an event from another service arrives through the REAL
/// <see cref="InboxDispatchWorker"/> against real PostgreSQL, a receptor fires at a lifecycle stage,
/// and it sends a command the service owns, handled here by a receptor that returns no result. The
/// command must be handled exactly once, in-process, and never written to the outbox. Before the fix
/// SendAsync answered Accepted and dropped the command, from every stage alike; the test fixtures
/// that hid it ran without owned domains, which this one configures the way a deployed service does.
/// </summary>
/// <remarks>
/// The inbox worker fires the PreInbox and PostInbox stages, and for an event with no perspectives
/// also PostAllPerspectives and PostLifecycle, each as an inline and a detached variant. Detached
/// stages run on a fresh task and scope, so every assertion waits on the owned command's own
/// completion signal, never on time.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/InboxDispatchWorker.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class LifecycleStageOwnedCommandSendIntegrationTests : EFCoreTestBase {
  private const string OWNED_DOMAIN = "Whizbang.Data.EFCore.Postgres.Tests.StageSends.Owned";

  [Test]
  [Arguments(LifecycleStage.PreInboxInline)]
  [Arguments(LifecycleStage.PreInboxDetached)]
  [Arguments(LifecycleStage.PostInboxInline)]
  [Arguments(LifecycleStage.PostInboxDetached)]
  [Arguments(LifecycleStage.PostAllPerspectivesInline)]
  [Arguments(LifecycleStage.PostAllPerspectivesDetached)]
  [Arguments(LifecycleStage.PostLifecycleInline)]
  [Arguments(LifecycleStage.PostLifecycleDetached)]
  public async Task ReceptorAtStage_SendsOwnedVoidCommand_HandledOnceAndNeverInTheOutboxAsync(LifecycleStage stage) {
    await _assertOwnedCommandHandledOnceAsync(StageTriggers.For(stage, asSystemForTenant: false));
  }

  [Test]
  [Arguments(LifecycleStage.PostInboxDetached)]
  [Arguments(LifecycleStage.PostAllPerspectivesDetached)]
  public async Task ReceptorAtStage_SendsOwnedVoidCommandAsSystemForTenant_HandledOnceAndNeverInTheOutboxAsync(LifecycleStage stage) {
    await _assertOwnedCommandHandledOnceAsync(StageTriggers.For(stage, asSystemForTenant: true));
  }

  private async Task _assertOwnedCommandHandledOnceAsync(object trigger) {
    var (serviceProvider, jsonOptions) = await _createServicesAsync();
    var id = StageTriggers.IdOf(trigger);
    var handled = StageSendProbe.Expect(id);

    var work = new InboxWork {
      MessageId = (Guid)TrackedGuid.New(),
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From((Guid)TrackedGuid.New()),
        Payload = JsonSerializer.SerializeToElement(trigger, trigger.GetType(), jsonOptions),
        Hops = [new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = Guid.CreateVersion7(),
            ServiceName = "upstream-service",
            HostName = "upstream-host",
            ProcessId = 1
          },
          CorrelationId = CorrelationId.New(),
          CausationId = MessageId.New(),
          Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "tenant-stage-sends", UserId = "user-stage-sends" })
        }],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox }
      },
      MessageType = TypeNameFormatter.AssemblyQualifiedName(trigger.GetType()),
      StreamId = id,
      PartitionNumber = 0,
      Attempts = 1,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    var inbox = new FakeInboxChannelWriter();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    ScopeContextAccessor.CurrentInitiatingContext = null;
    ScopeContextAccessor.CurrentContext = null;

    var worker = new InboxDispatchWorker(
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: serviceProvider.GetRequiredService<IServiceInstanceProvider>(),
      inboxChannelWriter: inbox,
      handlerCommitChannel: new NullHandlerCommitChannel(),
      failureChannel: new NullFailureChannel(),
      schemaReadyGate: gate,
      options: Options.Create(new InboxDispatchWorkerOptions()),
      coordinatorOptions: Options.Create(new WorkCoordinatorOptions()),
      logger: NullLogger<InboxDispatchWorker>.Instance,
      integrityOptions: Options.Create(new StreamIntegrityOptions()),
      lifecycleMessageDeserializer: serviceProvider.GetRequiredService<ILifecycleMessageDeserializer>(),
      leaseHandleOptions: Options.Create(new LeaseHandleOptions { LeaseGraceSeconds = 30, MaxRenewalsPerWork = 6 }),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions { LeaseSeconds = 60 }),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      discardPolicy: new MessageDiscardPolicy(new PermissiveReceptorRegistryQuery(), NullLogger<MessageDiscardPolicy>.Instance, new System.Diagnostics.Metrics.Meter("test"), Options.Create(new RoutingOptions()), new EventMarkerResolver(NullMessageTypeCatalog.Instance)),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      leaseRegistry: new LeaseRegistry());

    using var cts = new CancellationTokenSource();
    try {
      await worker.StartAsync(cts.Token);
      await inbox.WriteAsync(work, cts.Token);

      // The owned command's receptor completing is the transition under test; a send the
      // dispatcher dropped or threw on never completes it (the hook reports a throw as a fault)
      await handled.Task.WaitAsync(TimeSpan.FromSeconds(30));

      await Assert.That(StageSendProbe.CountFor(id)).IsEqualTo(1)
        .Because("the owned command must be handled exactly once, in-process");

      await using var dbContext = CreateDbContext();
      var commandType = TypeNameFormatter.AssemblyQualifiedName(typeof(OwnedStageCommand));
      var outboxCopies = await dbContext.Outbox.CountAsync(m => m.MessageType == commandType);
      await Assert.That(outboxCopies).IsEqualTo(0)
        .Because("a command the service owns and handles never goes to the outbox");
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
      ScopeContextAccessor.CurrentInitiatingContext = null;
      ScopeContextAccessor.CurrentContext = null;
      await serviceProvider.DisposeAsync();
    }
  }

  private async Task<(ServiceProvider Provider, JsonSerializerOptions JsonOptions)> _createServicesAsync() {
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

    services.AddReceptors();
    services.AddWhizbangDispatcher();
    services.AddWhizbangLifecycleMessageDeserializer();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();

    return (services.BuildServiceProvider(), jsonOptions);
  }

  private sealed class FakeInboxChannelWriter : IInboxChannelWriter {
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(InboxWork work) => _channel.Writer.TryWrite(work);
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }

  private sealed class NullHandlerCommitChannel : IInboxHandlerCommitChannel {
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  private sealed class NullFailureChannel : IFailureChannel {
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }
}
