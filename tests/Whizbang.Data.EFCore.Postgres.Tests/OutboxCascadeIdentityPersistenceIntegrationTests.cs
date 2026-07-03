using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests verifying that the <em>cascade identity</em> (CorrelationId + CausationId) survives
/// outbox database persistence exactly the way <see cref="ScopeContextPersistenceIntegrationTests"/> proves
/// scope does. This reproduces the production defect where every persisted hop carried <c>sc</c> (scope) but
/// dropped <c>co</c>/<c>ca</c> — because the outbox hop builders re-derived scope from ambient context but
/// never the correlation/causation pair, so the SignalR notification's correlationId came back null and the
/// "Template Activating" toast never dismissed.
/// </summary>
/// <remarks>
/// Real PostgreSQL via Testcontainers (L6 — no DB mocking). The outbox hop is built in the driver-agnostic
/// dispatcher (<c>Dispatcher._createOutboxEnvelopeWithHop</c> / <c>_addOutboxHop</c>); this test drives it
/// through the real <see cref="EnvelopeSerializer"/> + <c>EFCoreWorkCoordinator</c> and reads the hop back
/// out of the outbox table, so it also proves the identity survives the JSONB round-trip.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
public class OutboxCascadeIdentityPersistenceIntegrationTests : EFCoreTestBase {

  public record IdentityTestEvent([property: StreamId] Guid Id) : IEvent;

  /// <summary>
  /// A published event that carries auto-populate properties — the shape of a a consumer <c>BaseConsumerEvent</c>: the
  /// SignalR notification hook reads <c>CorrelationId</c> off the OBJECT, so the publish path must run
  /// AutoPopulate (not just stamp the hop) or the toast's notification carries a null correlationId.
  /// </summary>
  public class IdentityPopulatedEvent : IEvent {
    [StreamId] public Guid Id { get; set; }

    [PopulateFromIdentifier(IdentifierKind.CorrelationId)]
    public string? CorrelationId { get; set; }

    [PopulateTimestamp(TimestampKind.SentAt)]
    public DateTimeOffset? SentAt { get; set; }
  }

  [Test]
  public async Task Publish_RunsAutoPopulate_SoEventObjectCarriesCorrelation_ForNotificationHookAsync() {
    // Arrange — the notification hook reads baseEvent.CorrelationId off the persisted OBJECT, so PublishAsync
    // must AutoPopulate the object from the (now correctly-stamped) hop, exactly like _createEnvelope does.
    var services = await _createServicesAsync();
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();
    var serviceProvider = services.BuildServiceProvider();

    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();
    var expectedCorrelation = CorrelationId.New();
    ScopeContextAccessor.CurrentInitiatingContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = expectedCorrelation,
      CausationId = MessageId.New()
    };

    try {
      // Act
      await dispatcher.PublishAsync(new IdentityPopulatedEvent { Id = Guid.CreateVersion7() });

      // Assert — read the persisted outbox payload and confirm the OBJECT carries the correlation + SentAt.
      await using var dbContext = CreateDbContext();
      var outboxMessages = await dbContext.Outbox.ToListAsync();
      var expectedType = typeof(IdentityPopulatedEvent).AssemblyQualifiedName;
      var ourMessage = outboxMessages.FirstOrDefault(m => m.MessageType == expectedType);
      await Assert.That(ourMessage).IsNotNull()
        .Because("Published event should be persisted in the outbox.");

      var payload = ourMessage!.MessageData.Payload;
      var objCorrelation = payload.TryGetProperty("CorrelationId", out var c) && c.ValueKind == JsonValueKind.String
        ? c.GetString()
        : null;
      await Assert.That(objCorrelation).IsEqualTo(expectedCorrelation.Value.ToString())
        .Because("PublishAsync must AutoPopulate the event object's CorrelationId (read by the notification hook), not just the hop.");

      var sentAt = payload.TryGetProperty("SentAt", out var s) && s.ValueKind == JsonValueKind.String
        ? s.GetDateTimeOffset()
        : default;
      await Assert.That(sentAt).IsNotEqualTo(default(DateTimeOffset))
        .Because("PublishAsync must run the same SentAt-phase AutoPopulate as dispatch — SentAt was left at 0001-01-01 (the production signature).");
    } finally {
      ScopeContextAccessor.CurrentInitiatingContext = null;
      await serviceProvider.DisposeAsync();
    }
  }

  /// <summary>Command whose receptor emits an event that cascades to the outbox (the production shape).</summary>
  public record IdentityCascadeCommand([property: StreamId] Guid Id);

  /// <summary>Event returned by the receptor, cascaded to outbox with default routing.</summary>
  public record IdentityCascadeEvent([property: StreamId] Guid Id) : IEvent;

  /// <summary>Non-void receptor: emits an event while handling the command (mirrors a JobService receptor).</summary>
  public class IdentityCascadeCommandHandler : IReceptor<IdentityCascadeCommand, IdentityCascadeEvent> {
    public ValueTask<IdentityCascadeEvent> HandleAsync(IdentityCascadeCommand command, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new IdentityCascadeEvent(command.Id));
    }
  }

  [Test]
  public async Task Cascade_ReceptorEmittedEvent_InheritsInboundCorrelation_OnOutboxHopAsync() {
    // Arrange — the exact production scenario: an inbound command carries a correlation; the receptor emits an
    // event; that cascaded event's outbox hop must inherit the inbound correlation (so the SignalR completion
    // notification carries a matching correlationId and the toast dismisses).
    var services = await _createServicesAsync();
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();
    var serviceProvider = services.BuildServiceProvider();

    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();
    var command = new IdentityCascadeCommand(Guid.CreateVersion7());
    var expectedCorrelation = CorrelationId.New();

    try {
      // Act — dispatch the command under an explicit inbound correlation.
      await dispatcher.LocalInvokeAsync(command, MessageContext.Create(expectedCorrelation));

      // Assert — the cascaded event in the outbox carries the inbound correlation on its hop.
      await using var dbContext = CreateDbContext();
      var outboxMessages = await dbContext.Outbox.ToListAsync();
      var expectedType = typeof(IdentityCascadeEvent).AssemblyQualifiedName;
      var ourMessage = outboxMessages.FirstOrDefault(m => m.MessageType == expectedType);
      await Assert.That(ourMessage).IsNotNull()
        .Because("The receptor's cascaded event should be in the outbox.");

      var envelope = new MessageEnvelope<JsonElement> {
        MessageId = ourMessage!.MessageData.MessageId,
        Payload = ourMessage.MessageData.Payload,
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        Hops = ourMessage.MessageData.Hops
      };
      await Assert.That(envelope.GetCorrelationId()).IsEqualTo(expectedCorrelation)
        .Because("A receptor-emitted event must inherit the inbound correlation onto its outbox hop — the cascade seam that was dropping it.");
    } finally {
      await serviceProvider.DisposeAsync();
    }
  }

  [Test]
  public async Task Outbox_WithAmbientInitiatingContext_PersistsCorrelationAndCausation_OnHopAsync() {
    // Arrange
    var services = await _createServicesAsync();
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();
    var serviceProvider = services.BuildServiceProvider();

    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();
    var testEvent = new IdentityTestEvent(Guid.CreateVersion7());

    // Simulate a message having been received at this service (e.g. the Activate command arriving over the
    // transport): SecurityContextHelper sets CurrentInitiatingContext with the inbound correlation/causation.
    // Any event this handler emits must inherit that identity onto its outbox hop.
    var expectedCorrelation = CorrelationId.New();
    var initiatingMessageId = MessageId.New();
    var scope = new PerspectiveScope { UserId = "user-123", TenantId = "tenant-456" };
    var extraction = new SecurityExtraction {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test"
    };
    var scopeContext = new ImmutableScopeContext(extraction, shouldPropagate: true);
    var initiatingContext = new MessageContext {
      MessageId = initiatingMessageId,
      CorrelationId = expectedCorrelation,
      CausationId = MessageId.New(),
      ScopeContext = scopeContext
    };
    ScopeContextAccessor.CurrentContext = scopeContext;
    ScopeContextAccessor.CurrentInitiatingContext = initiatingContext;

    try {
      // Act
      await dispatcher.PublishAsync(testEvent);

      // Assert — read the persisted outbox hop back out of PostgreSQL
      await using var dbContext = CreateDbContext();
      var outboxMessages = await dbContext.Outbox.ToListAsync();
      var expectedType = typeof(IdentityTestEvent).AssemblyQualifiedName;
      var ourMessage = outboxMessages.FirstOrDefault(m => m.MessageType == expectedType);
      await Assert.That(ourMessage).IsNotNull()
        .Because("Published event should be persisted in the outbox");

      var envelope = new MessageEnvelope<JsonElement> {
        MessageId = ourMessage!.MessageData.MessageId,
        Payload = ourMessage.MessageData.Payload,
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        Hops = ourMessage.MessageData.Hops
      };

      // The regression: today the outbox hop carries scope but NOT correlation/causation, so this is null.
      await Assert.That(envelope.GetCorrelationId()).IsEqualTo(expectedCorrelation)
        .Because("The outbox hop must inherit CorrelationId from the ambient initiating context, the same way it inherits scope.");

      var hopWithIdentity = ourMessage.MessageData.Hops.FirstOrDefault(h => h.CorrelationId is not null);
      await Assert.That(hopWithIdentity).IsNotNull()
        .Because("At least one persisted hop must carry the cascade identity after the JSONB round-trip.");
      await Assert.That(hopWithIdentity!.CausationId).IsNotNull()
        .Because("CausationId is stripped alongside CorrelationId at the same seam — both must survive.");
    } finally {
      ScopeContextAccessor.CurrentContext = null;
      ScopeContextAccessor.CurrentInitiatingContext = null;
      await serviceProvider.DisposeAsync();
    }
  }

  private async Task<ServiceCollection> _createServicesAsync() {
    await base.SetupAsync();

    var services = new ServiceCollection();

    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));

    services.AddScoped(_ => CreateDbContext());

    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    services.AddSingleton(jsonOptions);
    services.AddSingleton<IEnvelopeSerializer, EnvelopeSerializer>();
    services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));

    services.AddScoped<IWorkCoordinator>(sp => {
      var dbContext = sp.GetRequiredService<WorkCoordinationDbContext>();
      return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, jsonOptions);
    });

    services.AddScoped<IWorkCoordinatorStrategy>(sp => {
      var coordinator = sp.GetRequiredService<IWorkCoordinator>();
      var instanceProvider = sp.GetRequiredService<IServiceInstanceProvider>();
      var logger = sp.GetService<ILogger<ScopedWorkCoordinatorStrategy>>();
      var options = new WorkCoordinatorOptions {
        LeaseSeconds = 30,
        AbandonStaleInstanceThresholdSeconds = 300,
        PartitionCount = 4
      };
      return new ScopedWorkCoordinatorStrategy(
        coordinator,
        instanceProvider,
        workChannelWriter: null,
        options,
        logger
      );
    });

    services.AddReceptors();
    services.AddWhizbangDispatcher();

    return services;
  }
}
