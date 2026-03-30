using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
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
/// Integration tests verifying that ScopeDelta (security context) survives
/// outbox database persistence round-trip through PostgreSQL JSONB.
/// </summary>
/// <remarks>
/// After lifecycle invoker convergence, all stages flow through ReceptorInvoker
/// which establishes scope context from envelope hops. These tests verify the
/// persistence layer correctly stores and retrieves ScopeDelta.
/// </remarks>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
public class ScopeContextPersistenceIntegrationTests : EFCoreTestBase {

  #region Test Types

  public record ScopeTestEvent([property: StreamId] Guid Id) : IEvent;

  /// <summary>
  /// Command handled by a non-void receptor. Used to test the void-with-any-invoker cascade
  /// (LocalInvokeAsync → _localInvokeVoidWithAnyInvokerAndTracingAsync) where
  /// the source envelope must carry scope for cascaded events.
  /// </summary>
  public record ScopeCascadeCommand([property: StreamId] Guid Id);

  /// <summary>
  /// Event returned by the receptor, cascaded to outbox with default routing.
  /// </summary>
  public record ScopeCascadeEvent([property: StreamId] Guid Id) : IEvent;

  /// <summary>
  /// Non-void receptor: returns an event that cascades to outbox.
  /// </summary>
  public class ScopeCascadeCommandHandler : IReceptor<ScopeCascadeCommand, ScopeCascadeEvent> {
    public ValueTask<ScopeCascadeEvent> HandleAsync(ScopeCascadeCommand command, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new ScopeCascadeEvent(command.Id));
    }
  }

  #endregion

  #region Outbox Persistence Tests

  [Test]
  public async Task Outbox_WithUserScope_PersistsScopeDelta_InDatabaseAsync() {
    // Arrange
    var services = await _createServicesAsync();
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();
    var serviceProvider = services.BuildServiceProvider();

    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();
    var testEvent = new ScopeTestEvent(Guid.CreateVersion7());

    // Set scope context to simulate authenticated user dispatch
    var userScope = new PerspectiveScope { UserId = "user-123", TenantId = "tenant-456" };
    var extraction = new SecurityExtraction {
      Scope = userScope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test"
    };
    var scopeContext = new ImmutableScopeContext(extraction, shouldPropagate: true);
    var messageContext = new MessageContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      ScopeContext = scopeContext
    };
    ScopeContextAccessor.CurrentInitiatingContext = messageContext;

    try {
      // Act
      await dispatcher.AsSystem().ForTenant("tenant-456").PublishAsync(testEvent);

      // Assert - Query database for stored message
      await using var dbContext = CreateDbContext();
      var outboxMessages = await dbContext.Outbox.ToListAsync();

      await Assert.That(outboxMessages).Count().IsGreaterThan(0)
        .Because("Event should be persisted in outbox");

      var expectedType = typeof(ScopeTestEvent).AssemblyQualifiedName;
      var ourMessage = outboxMessages.FirstOrDefault(m => m.MessageType == expectedType);
      await Assert.That(ourMessage).IsNotNull();

      // Verify ScopeDelta is stored on hops
      var hopsWithScope = ourMessage!.MessageData.Hops.Where(h => h.Scope != null).ToList();
      await Assert.That(hopsWithScope).Count().IsGreaterThan(0)
        .Because("At least one hop should carry ScopeDelta from AsSystem()");

      // Reconstruct envelope and verify scope via GetCurrentScope()
      var envelope = new MessageEnvelope<JsonElement> {
        MessageId = ourMessage.MessageData.MessageId,
        Payload = ourMessage.MessageData.Payload,
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        Hops = ourMessage.MessageData.Hops
      };
      var scope = envelope.GetCurrentScope();
      await Assert.That(scope).IsNotNull()
        .Because("ScopeDelta should survive PostgreSQL JSONB round-trip");
      await Assert.That(scope!.Scope.UserId).IsEqualTo("SYSTEM")
        .Because("AsSystem() sets UserId to SYSTEM");
    } finally {
      ScopeContextAccessor.CurrentInitiatingContext = null;
      await serviceProvider.DisposeAsync();
    }
  }

  [Test]
  public async Task Outbox_WithSystemScope_PersistsScopeDelta_InDatabaseAsync() {
    // Arrange
    var services = await _createServicesAsync();
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();
    var serviceProvider = services.BuildServiceProvider();

    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();
    var testEvent = new ScopeTestEvent(Guid.CreateVersion7());

    try {
      // Act - Publish with AsSystem scope
      await dispatcher.AsSystem().ForTenant("system-tenant").PublishAsync(testEvent);

      // Assert
      await using var dbContext = CreateDbContext();
      var outboxMessages = await dbContext.Outbox.ToListAsync();
      var expectedType = typeof(ScopeTestEvent).AssemblyQualifiedName;
      var ourMessage = outboxMessages.FirstOrDefault(m => m.MessageType == expectedType);
      await Assert.That(ourMessage).IsNotNull();

      var envelope = new MessageEnvelope<JsonElement> {
        MessageId = ourMessage!.MessageData.MessageId,
        Payload = ourMessage.MessageData.Payload,
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        Hops = ourMessage.MessageData.Hops
      };
      var scope = envelope.GetCurrentScope();
      await Assert.That(scope).IsNotNull();
      await Assert.That(scope!.Scope.UserId).IsEqualTo("SYSTEM");
      await Assert.That(scope.Scope.TenantId).IsEqualTo("system-tenant");
    } finally {
      await serviceProvider.DisposeAsync();
    }
  }

  [Test]
  public async Task Outbox_ScopeDeltaValues_PreservedAfterJsonbRoundTripAsync() {
    // Arrange - Verify that ScopeDelta.Values (Dictionary<ScopeProp, ...>) survive
    // PostgreSQL JSONB serialization/deserialization with enum keys.
    var services = await _createServicesAsync();
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();
    var serviceProvider = services.BuildServiceProvider();

    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();
    var testEvent = new ScopeTestEvent(Guid.CreateVersion7());

    try {
      // Act
      await dispatcher.AsSystem().ForTenant("jsonb-test-tenant").PublishAsync(testEvent);

      // Assert - Verify ScopeDelta.Values survive JSONB round-trip
      await using var dbContext = CreateDbContext();
      var outboxMessages = await dbContext.Outbox.ToListAsync();
      var expectedType = typeof(ScopeTestEvent).AssemblyQualifiedName;
      var ourMessage = outboxMessages.FirstOrDefault(m => m.MessageType == expectedType);
      await Assert.That(ourMessage).IsNotNull();

      // Verify that ScopeDelta.Values dictionary has ScopeProp.Scope key
      var hopWithScope = ourMessage!.MessageData.Hops.FirstOrDefault(h => h.Scope != null);
      await Assert.That(hopWithScope).IsNotNull()
        .Because("Hop should carry ScopeDelta after JSONB round-trip");
      await Assert.That(hopWithScope!.Scope!.Values).IsNotNull()
        .Because("ScopeDelta.Values should not be null after JSONB deserialization");
      await Assert.That(hopWithScope.Scope.Values!.ContainsKey(ScopeProp.Scope)).IsTrue()
        .Because("ScopeDelta.Values should contain ScopeProp.Scope key after JSONB round-trip");
    } finally {
      await serviceProvider.DisposeAsync();
    }
  }

  #endregion

  #region Fast-Path Cascade Scope Tests

  /// <summary>
  /// Verifies that the LocalInvokeAsync fast path (void call on non-void receptor)
  /// creates a proper envelope with scope so cascaded events carry security context
  /// through to the outbox. Before the fix, these paths used a bare MessageEnvelope
  /// with empty hops, so cascaded events lost scope when ambient context was unavailable.
  /// </summary>
  [Test]
  public async Task LocalInvokeAsync_FastPath_CascadedEvent_CarriesScopeToOutboxAsync() {
    // Arrange
    var services = await _createServicesAsync();
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();
    var serviceProvider = services.BuildServiceProvider();

    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();
    var command = new ScopeCascadeCommand(Guid.CreateVersion7());

    // Set ambient scope to simulate authenticated handler context
    var handlerScope = new PerspectiveScope { UserId = "fast-path-user", TenantId = "fast-path-tenant" };
    var extraction = new SecurityExtraction {
      Scope = handlerScope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test"
    };
    var scopeContext = new ImmutableScopeContext(extraction, shouldPropagate: true);
    ScopeContextAccessor.CurrentContext = scopeContext;

    try {
      // Act — void LocalInvokeAsync hits the any-invoker tracing path (_localInvokeVoidWithAnyInvokerAndTracingAsync)
      // Receptor returns ScopeCascadeEvent which cascades to outbox
      await dispatcher.LocalInvokeAsync(command);

      // Assert — cascaded event in outbox should carry scope from envelope hop
      await using var dbContext = CreateDbContext();
      var outboxMessages = await dbContext.Outbox.ToListAsync();
      var expectedType = typeof(ScopeCascadeEvent).AssemblyQualifiedName;
      var ourMessage = outboxMessages.FirstOrDefault(m => m.MessageType == expectedType);
      await Assert.That(ourMessage).IsNotNull()
        .Because("Cascaded event from non-void receptor should be in outbox");

      var envelope = new MessageEnvelope<JsonElement> {
        MessageId = ourMessage!.MessageData.MessageId,
        Payload = ourMessage.MessageData.Payload,
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        Hops = ourMessage.MessageData.Hops
      };
      var scope = envelope.GetCurrentScope();
      await Assert.That(scope).IsNotNull()
        .Because("Cascaded event should carry scope from fast-path envelope (not bare envelope with empty hops)");
      await Assert.That(scope!.Scope.UserId).IsEqualTo("fast-path-user");
      await Assert.That(scope.Scope.TenantId).IsEqualTo("fast-path-tenant");
    } finally {
      ScopeContextAccessor.CurrentContext = null;
      await serviceProvider.DisposeAsync();
    }
  }

  /// <summary>
  /// Same as above but with AsSystem() scope, verifying SYSTEM context propagates
  /// through the fast-path cascade to outbox.
  /// </summary>
  [Test]
  public async Task LocalInvokeAsync_FastPath_CascadedEvent_CarriesSystemScopeToOutboxAsync() {
    // Arrange
    var services = await _createServicesAsync();
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();
    var serviceProvider = services.BuildServiceProvider();

    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();
    var command = new ScopeCascadeCommand(Guid.CreateVersion7());

    // Set ambient SYSTEM scope
    var systemScope = new PerspectiveScope { UserId = "SYSTEM", TenantId = "*" };
    var extraction = new SecurityExtraction {
      Scope = systemScope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test"
    };
    var scopeContext = new ImmutableScopeContext(extraction, shouldPropagate: true);
    ScopeContextAccessor.CurrentContext = scopeContext;

    try {
      // Act
      await dispatcher.LocalInvokeAsync(command);

      // Assert
      await using var dbContext = CreateDbContext();
      var outboxMessages = await dbContext.Outbox.ToListAsync();
      var expectedType = typeof(ScopeCascadeEvent).AssemblyQualifiedName;
      var ourMessage = outboxMessages.FirstOrDefault(m => m.MessageType == expectedType);
      await Assert.That(ourMessage).IsNotNull();

      var envelope = new MessageEnvelope<JsonElement> {
        MessageId = ourMessage!.MessageData.MessageId,
        Payload = ourMessage.MessageData.Payload,
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        Hops = ourMessage.MessageData.Hops
      };
      var scope = envelope.GetCurrentScope();
      await Assert.That(scope).IsNotNull()
        .Because("SYSTEM scope should propagate through fast-path cascade to outbox");
      await Assert.That(scope!.Scope.UserId).IsEqualTo("SYSTEM");
      await Assert.That(scope.Scope.TenantId).IsEqualTo("*");
    } finally {
      ScopeContextAccessor.CurrentContext = null;
      await serviceProvider.DisposeAsync();
    }
  }

  #endregion

  #region Helper Methods

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
        StaleThresholdSeconds = 300,
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

  #endregion
}
