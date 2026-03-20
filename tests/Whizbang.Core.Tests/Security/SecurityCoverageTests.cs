using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Extractors;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Additional coverage tests for MessageHopSecurityExtractor and SecurityContextHelper.
/// Targets uncovered paths including logging branches, edge cases, and callback scenarios.
/// </summary>
[Category("Security")]
public class SecurityCoverageTests {
  // ========================================
  // MessageHopSecurityExtractor - Claims Extraction
  // ========================================

  [Test]
  public async Task Extractor_WithClaimsInScopeDelta_ExtractsClaimsAsync() {
    // Arrange: ScopeDelta with claims collection
    var extractor = new MessageHopSecurityExtractor();
    var options = new MessageSecurityOptions();

    var scopeElement = System.Text.Json.JsonSerializer.SerializeToElement(
      new { t = "tenant-1", u = "user-1" });
    var claimsDict = new Dictionary<string, string> { ["email"] = "user@example.com", ["role"] = "admin" };
    var claimsElement = System.Text.Json.JsonSerializer.SerializeToElement(claimsDict);

    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance(),
      Timestamp = DateTimeOffset.UtcNow,
      Scope = new ScopeDelta {
        Values = new Dictionary<ScopeProp, System.Text.Json.JsonElement> {
          [ScopeProp.Scope] = scopeElement
        },
        Collections = new Dictionary<ScopeProp, CollectionChanges> {
          [ScopeProp.Claims] = new CollectionChanges { Add = claimsElement }
        }
      }
    };

    var envelope = new MessageEnvelope<CoverageTestMessage> {
      MessageId = MessageId.New(),
      Payload = new CoverageTestMessage("test"),
      Hops = [hop]
    };

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Claims.Count).IsEqualTo(2);
    await Assert.That(result.Claims["email"]).IsEqualTo("user@example.com");
    await Assert.That(result.Claims["role"]).IsEqualTo("admin");
  }

  [Test]
  public async Task Extractor_WithSecurityPrincipalsInScopeDelta_ExtractsPrincipalsAsync() {
    // Arrange: ScopeDelta with security principals collection
    var extractor = new MessageHopSecurityExtractor();
    var options = new MessageSecurityOptions();

    var scopeElement = System.Text.Json.JsonSerializer.SerializeToElement(
      new { t = "tenant-1", u = "user-1" });
    string[] principalIds = ["principal-1", "principal-2"];
    var principalsElement = System.Text.Json.JsonSerializer.SerializeToElement(principalIds);

    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance(),
      Timestamp = DateTimeOffset.UtcNow,
      Scope = new ScopeDelta {
        Values = new Dictionary<ScopeProp, System.Text.Json.JsonElement> {
          [ScopeProp.Scope] = scopeElement
        },
        Collections = new Dictionary<ScopeProp, CollectionChanges> {
          [ScopeProp.Principals] = new CollectionChanges {
            Set = principalsElement
          }
        }
      }
    };

    var envelope = new MessageEnvelope<CoverageTestMessage> {
      MessageId = MessageId.New(),
      Payload = new CoverageTestMessage("test"),
      Hops = [hop]
    };

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.SecurityPrincipals.Count).IsEqualTo(2);
  }

  [Test]
  public async Task Extractor_WithLogger_MultipleHops_LogsProcessingAndExtractionAsync() {
    // Arrange: Multiple hops with logger to cover ProcessingHops + ScopeExtracted logging
    var logger = NullLogger<MessageHopSecurityExtractor>.Instance;
    var extractor = new MessageHopSecurityExtractor(logger);
    var options = new MessageSecurityOptions();

    var firstHop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance("service-1"),
      Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5),
      Scope = ScopeDelta.FromSecurityContext(new SecurityContext {
        TenantId = "tenant-a",
        UserId = "user-a"
      })
    };
    var secondHop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance("service-2"),
      Timestamp = DateTimeOffset.UtcNow,
      Scope = ScopeDelta.FromSecurityContext(new SecurityContext {
        TenantId = "tenant-b",
        UserId = "user-b"
      })
    };

    var envelope = new MessageEnvelope<CoverageTestMessage> {
      MessageId = MessageId.New(),
      Payload = new CoverageTestMessage("test"),
      Hops = [firstHop, secondHop]
    };

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert - should merge and return last hop values
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope.TenantId).IsEqualTo("tenant-b");
    await Assert.That(result.Scope.UserId).IsEqualTo("user-b");
  }

  [Test]
  public async Task Extractor_WithLogger_EmptyTenantAndUser_ReturnsNullAndLogsNoScopeFoundAsync() {
    // Arrange: Scope with empty strings for both TenantId and UserId
    var logger = NullLogger<MessageHopSecurityExtractor>.Instance;
    var extractor = new MessageHopSecurityExtractor(logger);
    var options = new MessageSecurityOptions();

    var envelope = _createEnvelopeWithSecurityContext(new SecurityContext {
      TenantId = "",
      UserId = ""
    });

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert - empty strings should return null (covers NoScopeFound log with logger)
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task Extractor_WithLogger_MixedHopTypes_LogsProcessingHopCountsAsync() {
    // Arrange: Mix of Current, Causation, and hops with null/empty scope - covers all log branches
    var logger = NullLogger<MessageHopSecurityExtractor>.Instance;
    var extractor = new MessageHopSecurityExtractor(logger);
    var options = new MessageSecurityOptions();

    var causationHop = new MessageHop {
      Type = HopType.Causation,
      ServiceInstance = _createServiceInstance("causation"),
      Timestamp = DateTimeOffset.UtcNow.AddMinutes(-10),
      Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "c-tenant" })
    };
    var nullScopeHop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance("null-scope"),
      Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5),
      Scope = null
    };
    var noChangesHop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance("no-changes"),
      Timestamp = DateTimeOffset.UtcNow.AddMinutes(-3),
      Scope = new ScopeDelta()
    };
    var validHop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance("valid"),
      Timestamp = DateTimeOffset.UtcNow,
      Scope = ScopeDelta.FromSecurityContext(new SecurityContext {
        TenantId = "valid-tenant",
        UserId = "valid-user"
      })
    };

    var envelope = new MessageEnvelope<CoverageTestMessage> {
      MessageId = MessageId.New(),
      Payload = new CoverageTestMessage("test"),
      Hops = [causationHop, nullScopeHop, noChangesHop, validHop]
    };

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert - only valid hop should contribute
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope.TenantId).IsEqualTo("valid-tenant");
    await Assert.That(result.Scope.UserId).IsEqualTo("valid-user");
  }

  [Test]
  public async Task Extractor_WithNullLogger_AllBranches_DoesNotThrowAsync() {
    // Arrange: No logger (null) - covers all the "if (logger != null)" false branches in Log wrappers
    var extractor = new MessageHopSecurityExtractor(null);
    var options = new MessageSecurityOptions();

    // Test null hops path
    var nullHopsEnvelope = new MessageEnvelope<CoverageTestMessage> {
      MessageId = MessageId.New(),
      Payload = new CoverageTestMessage("test"),
      Hops = null!
    };
    var result1 = await extractor.ExtractAsync(nullHopsEnvelope, options, CancellationToken.None);
    await Assert.That(result1).IsNull();

    // Test empty hops path
    var emptyHopsEnvelope = new MessageEnvelope<CoverageTestMessage> {
      MessageId = MessageId.New(),
      Payload = new CoverageTestMessage("test"),
      Hops = []
    };
    var result2 = await extractor.ExtractAsync(emptyHopsEnvelope, options, CancellationToken.None);
    await Assert.That(result2).IsNull();

    // Test hop with null scope
    var nullScopeEnvelope = new MessageEnvelope<CoverageTestMessage> {
      MessageId = MessageId.New(),
      Payload = new CoverageTestMessage("test"),
      Hops = [new MessageHop {
        Type = HopType.Current,
        ServiceInstance = _createServiceInstance(),
        Timestamp = DateTimeOffset.UtcNow,
        Scope = null
      }]
    };
    var result3 = await extractor.ExtractAsync(nullScopeEnvelope, options, CancellationToken.None);
    await Assert.That(result3).IsNull();

    // Test hop with no changes scope
    var noChangesEnvelope = new MessageEnvelope<CoverageTestMessage> {
      MessageId = MessageId.New(),
      Payload = new CoverageTestMessage("test"),
      Hops = [new MessageHop {
        Type = HopType.Current,
        ServiceInstance = _createServiceInstance(),
        Timestamp = DateTimeOffset.UtcNow,
        Scope = new ScopeDelta()
      }]
    };
    var result4 = await extractor.ExtractAsync(noChangesEnvelope, options, CancellationToken.None);
    await Assert.That(result4).IsNull();
  }

  [Test]
  public async Task Extractor_WithOnlyEmptyStringsScope_ReturnsNullAsync() {
    // Arrange: Scope where TenantId and UserId are empty strings (not null)
    var extractor = new MessageHopSecurityExtractor();
    var options = new MessageSecurityOptions();

    var securityContext = new SecurityContext {
      TenantId = "",
      UserId = ""
    };
    var envelope = _createEnvelopeWithSecurityContext(securityContext);

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert - empty strings should be treated as no identity (lines 62-64)
    await Assert.That(result).IsNull();
  }

  // ========================================
  // SecurityContextHelper - Callback Cancellation
  // ========================================

  [Test]
  public async Task Helper_EstablishFullContextAsync_CallbackCancellation_ThrowsAsync() {
    // Arrange: Callback that gets cancelled - covers cancellationToken.ThrowIfCancellationRequested() in callback loop
    var envelope = _createHelperEnvelopeWithScope("user-cancel", "tenant-cancel");

    var services = new ServiceCollection();
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [new NullTestExtractor()],
      callbacks: [],
      options: new MessageSecurityOptions { AllowAnonymous = true }
    );
    services.AddSingleton<IMessageSecurityContextProvider>(provider);
    services.AddSingleton<IScopeContextAccessor>(new ScopeContextAccessor());
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();

    // Register a callback that will be invoked (extractor returns null, envelope has scope)
    using var cts = new CancellationTokenSource();
    var callbackCount = 0;
    services.AddSingleton<ISecurityContextCallback>(
      new TestSecurityCallback(async (_, _, _, ct) => {
        callbackCount++;
        // Cancel after first callback executes
        await cts.CancelAsync();
      }));
    // Second callback should not execute due to cancellation check
    services.AddSingleton<ISecurityContextCallback>(
      new TestSecurityCallback((_, _, _, _) => {
        callbackCount++;
        return ValueTask.CompletedTask;
      }));
    var sp = services.BuildServiceProvider();

    // Act & Assert - should throw OperationCanceledException during callback loop
    await Assert.That(async () =>
      await SecurityContextHelper.EstablishFullContextAsync(envelope, sp, cts.Token)
    ).ThrowsExactly<OperationCanceledException>();

    // First callback executed, second should not have
    await Assert.That(callbackCount).IsEqualTo(1);
  }

  [Test]
  public async Task Helper_EstablishFullContextAsync_MultipleCallbacks_AllInvokedAsync() {
    // Arrange: Multiple callbacks all invoked when immutableScope is created
    var envelope = _createHelperEnvelopeWithScope("user-multi", "tenant-multi");

    var services = new ServiceCollection();
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [new NullTestExtractor()],
      callbacks: [],
      options: new MessageSecurityOptions { AllowAnonymous = true }
    );
    services.AddSingleton<IMessageSecurityContextProvider>(provider);
    services.AddSingleton<IScopeContextAccessor>(new ScopeContextAccessor());
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();

    var invocations = new List<string>();
    services.AddSingleton<ISecurityContextCallback>(
      new TestSecurityCallback((_, _, _, _) => {
        invocations.Add("callback-1");
        return ValueTask.CompletedTask;
      }));
    services.AddSingleton<ISecurityContextCallback>(
      new TestSecurityCallback((_, _, _, _) => {
        invocations.Add("callback-2");
        return ValueTask.CompletedTask;
      }));
    var sp = services.BuildServiceProvider();

    // Act
    await SecurityContextHelper.EstablishFullContextAsync(envelope, sp, CancellationToken.None);

    // Assert
    await Assert.That(invocations.Count).IsEqualTo(2);
    await Assert.That(invocations[0]).IsEqualTo("callback-1");
    await Assert.That(invocations[1]).IsEqualTo("callback-2");
  }

  // ========================================
  // SecurityContextHelper - _setMessageContextFromEnvelopeWithScope branches
  // ========================================

  [Test]
  public async Task Helper_EstablishFullContextAsync_NoMessageContextAccessor_WithImmutableScope_DoesNotThrowAsync() {
    // Arrange: Extractor returns null, envelope has scope, IScopeContextAccessor registered
    // but NO IMessageContextAccessor - covers null messageContextAccessor branch in _setMessageContextFromEnvelopeWithScope
    var envelope = _createHelperEnvelopeWithScope("user-no-mca", "tenant-no-mca");

    var services = new ServiceCollection();
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [new NullTestExtractor()],
      callbacks: [],
      options: new MessageSecurityOptions { AllowAnonymous = true }
    );
    services.AddSingleton<IMessageSecurityContextProvider>(provider);
    var capturingAccessor = new CapturingScopeContextAccessor();
    services.AddSingleton<IScopeContextAccessor>(capturingAccessor);
    // intentionally no IMessageContextAccessor
    var sp = services.BuildServiceProvider();

    // Act - should not throw
    await SecurityContextHelper.EstablishFullContextAsync(envelope, sp, CancellationToken.None);

    // Assert - IScopeContextAccessor should still be set with ImmutableScopeContext
    await Assert.That(capturingAccessor.CapturedContext).IsNotNull();
    await Assert.That(capturingAccessor.CapturedContext).IsTypeOf<ImmutableScopeContext>();
  }

  [Test]
  public async Task Helper_EstablishFullContextAsync_WithExtractorSuccess_NoScopeAccessor_DoesNotThrowAsync() {
    // Arrange: Extractor succeeds, no IScopeContextAccessor for _setMessageContextFromEnvelopeWithScope
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { TenantId = "t", UserId = "u" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test"
    };
    var envelope = _createHelperEnvelope(new CoverageTestMessage("test"));
    var services = new ServiceCollection();
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [new TestExtractorWithResult(100, extraction)],
      callbacks: [],
      options: new MessageSecurityOptions { AllowAnonymous = true }
    );
    services.AddSingleton<IMessageSecurityContextProvider>(provider);
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    // intentionally no IScopeContextAccessor
    var sp = services.BuildServiceProvider();

    // Act - should not throw (covers null scopeContextAccessor in _setMessageContextFromEnvelopeWithScope)
    await SecurityContextHelper.EstablishFullContextAsync(envelope, sp, CancellationToken.None);
  }

  // ========================================
  // SecurityContextHelper - EstablishMessageContextForCascade ScopeContextAccessor fallback
  // ========================================

  [Test]
  public async Task Helper_Cascade_WithScopeContextAccessorFallback_ExtractsUserIdAsync() {
    // Arrange: No parent MessageContextAccessor, but ScopeContextAccessor.CurrentContext has ImmutableScopeContext
    // Covers the else-if fallback at line 308
    MessageContextAccessor.CurrentContext = null;
    var fallbackExtraction = new SecurityExtraction {
      Scope = new PerspectiveScope { UserId = "fallback-user", TenantId = "fallback-tenant" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Fallback"
    };
    ScopeContextAccessor.CurrentContext = new ImmutableScopeContext(fallbackExtraction, shouldPropagate: true);

    try {
      // Act
      SecurityContextHelper.EstablishMessageContextForCascade(serviceProvider: null);

      // Assert
      var messageContext = MessageContextAccessor.CurrentContext;
      await Assert.That(messageContext).IsNotNull();
      await Assert.That(messageContext!.UserId).IsEqualTo("fallback-user");
      await Assert.That(messageContext.TenantId).IsEqualTo("fallback-tenant");
    } finally {
      ScopeContextAccessor.CurrentContext = null;
      ScopeContextAccessor.CurrentInitiatingContext = null;
      MessageContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task Helper_Cascade_WithScopeContextAccessorFallback_WithLogger_LogsFallbackAsync() {
    // Arrange: Same as above but with a logger to cover the logging branch at line 312
    MessageContextAccessor.CurrentContext = null;
    var fallbackExtraction = new SecurityExtraction {
      Scope = new PerspectiveScope { UserId = "fb-user", TenantId = "fb-tenant" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Fallback"
    };
    ScopeContextAccessor.CurrentContext = new ImmutableScopeContext(fallbackExtraction, shouldPropagate: true);

    var services = new ServiceCollection();
    services.AddLogging();
    var sp = services.BuildServiceProvider();

    try {
      // Act
      SecurityContextHelper.EstablishMessageContextForCascade(sp);

      // Assert
      var messageContext = MessageContextAccessor.CurrentContext;
      await Assert.That(messageContext).IsNotNull();
      await Assert.That(messageContext!.UserId).IsEqualTo("fb-user");
      await Assert.That(messageContext.TenantId).IsEqualTo("fb-tenant");
    } finally {
      ScopeContextAccessor.CurrentContext = null;
      ScopeContextAccessor.CurrentInitiatingContext = null;
      MessageContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task Helper_Cascade_WithExplicitNonImmutableContext_FallsBackToMessageContextAsync() {
    // Arrange: Explicit context exists on IScopeContextAccessor but is NOT ImmutableScopeContext
    // This covers the branch where hasExplicitContext=true but explicitContext is not ImmutableScopeContext
    // So userId/tenantId remain null and falls through to parent MessageContextAccessor check
    var nonImmutableContext = new ScopeContext {
      Scope = new PerspectiveScope { UserId = "non-immutable-user", TenantId = "non-immutable-tenant" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    var services = new ServiceCollection();
    services.AddSingleton<IScopeContextAccessor>(new MockScopeContextAccessor(nonImmutableContext));
    var sp = services.BuildServiceProvider();

    // No parent message context, no AsyncLocal scope
    MessageContextAccessor.CurrentContext = null;
    ScopeContextAccessor.CurrentContext = null;

    try {
      // Act
      SecurityContextHelper.EstablishMessageContextForCascade(sp);

      // Assert - userId/tenantId should be null since non-ImmutableScopeContext was not matched
      var messageContext = MessageContextAccessor.CurrentContext;
      await Assert.That(messageContext).IsNotNull();
      await Assert.That(messageContext!.UserId).IsNull();
      await Assert.That(messageContext.TenantId).IsNull();
    } finally {
      ScopeContextAccessor.CurrentContext = null;
      ScopeContextAccessor.CurrentInitiatingContext = null;
      MessageContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task Helper_Cascade_WithExplicitImmutableContext_WithLogger_LogsExplicitContextAsync() {
    // Arrange: Explicit ImmutableScopeContext with logger - covers logging branches at lines 276-280 and 289-291
    var explicitExtraction = new SecurityExtraction {
      Scope = new PerspectiveScope { UserId = "explicit-user", TenantId = "explicit-tenant" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Explicit"
    };
    var explicitContext = new ImmutableScopeContext(explicitExtraction, shouldPropagate: true);

    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IScopeContextAccessor>(new MockScopeContextAccessor(explicitContext));
    var sp = services.BuildServiceProvider();

    MessageContextAccessor.CurrentContext = null;

    try {
      // Act
      SecurityContextHelper.EstablishMessageContextForCascade(sp);

      // Assert
      var messageContext = MessageContextAccessor.CurrentContext;
      await Assert.That(messageContext).IsNotNull();
      await Assert.That(messageContext!.UserId).IsEqualTo("explicit-user");
      await Assert.That(messageContext.TenantId).IsEqualTo("explicit-tenant");
    } finally {
      ScopeContextAccessor.CurrentContext = null;
      ScopeContextAccessor.CurrentInitiatingContext = null;
      MessageContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task Helper_Cascade_WithParentMessageContext_WithLogger_LogsReadFromMessageContextAsync() {
    // Arrange: Parent message context with logger - covers lines 296-305
    var services = new ServiceCollection();
    services.AddLogging();
    var sp = services.BuildServiceProvider();

    MessageContextAccessor.CurrentContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      Timestamp = DateTimeOffset.UtcNow,
      UserId = "parent-user",
      TenantId = "parent-tenant"
    };

    try {
      // Act
      SecurityContextHelper.EstablishMessageContextForCascade(sp);

      // Assert
      var messageContext = MessageContextAccessor.CurrentContext;
      await Assert.That(messageContext).IsNotNull();
      await Assert.That(messageContext!.UserId).IsEqualTo("parent-user");
      await Assert.That(messageContext.TenantId).IsEqualTo("parent-tenant");
    } finally {
      ScopeContextAccessor.CurrentContext = null;
      ScopeContextAccessor.CurrentInitiatingContext = null;
      MessageContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task Helper_Cascade_WithNullUserIdAndTenantId_WithLogger_LogsSkippingSetupAsync() {
    // Arrange: No parent context, no scope context - userId and tenantId are null
    // Covers the SkippingScopeContextSetup logging branch (lines 351-354)
    var services = new ServiceCollection();
    services.AddLogging();
    var sp = services.BuildServiceProvider();

    MessageContextAccessor.CurrentContext = null;
    ScopeContextAccessor.CurrentContext = null;

    try {
      // Act
      SecurityContextHelper.EstablishMessageContextForCascade(sp);

      // Assert - context is still set with null userId/tenantId
      var messageContext = MessageContextAccessor.CurrentContext;
      await Assert.That(messageContext).IsNotNull();
      await Assert.That(messageContext!.UserId).IsNull();
      await Assert.That(messageContext.TenantId).IsNull();
    } finally {
      ScopeContextAccessor.CurrentContext = null;
      ScopeContextAccessor.CurrentInitiatingContext = null;
      MessageContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task Helper_Cascade_WithExplicitContext_WithLogger_LogsSkippingDueToExplicitAsync() {
    // Arrange: Explicit context with logger - covers SkippingScopeContextDueToExplicit (lines 356-359)
    var explicitExtraction = new SecurityExtraction {
      Scope = new PerspectiveScope { UserId = "skip-user", TenantId = "skip-tenant" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Explicit"
    };
    var explicitContext = new ImmutableScopeContext(explicitExtraction, shouldPropagate: true);

    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IScopeContextAccessor>(new MockScopeContextAccessor(explicitContext));
    var sp = services.BuildServiceProvider();

    MessageContextAccessor.CurrentContext = null;

    try {
      // Act
      SecurityContextHelper.EstablishMessageContextForCascade(sp);

      // Assert
      var messageContext = MessageContextAccessor.CurrentContext;
      await Assert.That(messageContext).IsNotNull();
      await Assert.That(messageContext!.UserId).IsEqualTo("skip-user");
    } finally {
      ScopeContextAccessor.CurrentContext = null;
      ScopeContextAccessor.CurrentInitiatingContext = null;
      MessageContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task Helper_Cascade_WithUserIdOnly_WithLogger_LogsCreatingScopeContextAsync() {
    // Arrange: Only userId set (from parent) with logger - covers CreatingScopeContext + ScopeContextEstablished log
    var services = new ServiceCollection();
    services.AddLogging();
    var sp = services.BuildServiceProvider();

    MessageContextAccessor.CurrentContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      Timestamp = DateTimeOffset.UtcNow,
      UserId = "cascade-user",
      TenantId = null
    };

    try {
      // Act
      SecurityContextHelper.EstablishMessageContextForCascade(sp);

      // Assert
      var scopeContext = ScopeContextAccessor.CurrentContext;
      await Assert.That(scopeContext).IsNotNull();
      await Assert.That(scopeContext!.Scope.UserId).IsEqualTo("cascade-user");
    } finally {
      ScopeContextAccessor.CurrentContext = null;
      ScopeContextAccessor.CurrentInitiatingContext = null;
      MessageContextAccessor.CurrentContext = null;
    }
  }

  // ========================================
  // SecurityContextHelper - EstablishFullContextAsync immutable scope with full extraction
  // ========================================

  [Test]
  public async Task Helper_EstablishFullContextAsync_EnvelopeHasScopeWithRoles_ImmutableScopePreservesRolesAsync() {
    // Arrange: Extractor returns null, envelope has scope with roles/permissions/claims
    // Tests that the ImmutableScopeContext wrapping preserves all extraction fields (lines 158-169)
    var scopeElement = System.Text.Json.JsonSerializer.SerializeToElement(
      new { t = "tenant-full", u = "user-full" });
    string[] roles = ["Admin", "Editor"];
    var rolesElement = System.Text.Json.JsonSerializer.SerializeToElement(roles);
    string[] perms = ["read", "write"];
    var permsElement = System.Text.Json.JsonSerializer.SerializeToElement(perms);

    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance(),
      Timestamp = DateTimeOffset.UtcNow,
      Scope = new ScopeDelta {
        Values = new Dictionary<ScopeProp, System.Text.Json.JsonElement> {
          [ScopeProp.Scope] = scopeElement
        },
        Collections = new Dictionary<ScopeProp, CollectionChanges> {
          [ScopeProp.Roles] = new CollectionChanges { Set = rolesElement },
          [ScopeProp.Perms] = new CollectionChanges { Set = permsElement }
        }
      }
    };

    var envelope = new MessageEnvelope<CoverageTestMessage> {
      MessageId = MessageId.New(),
      Payload = new CoverageTestMessage("test"),
      Hops = [hop]
    };

    var services = new ServiceCollection();
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [new NullTestExtractor()],
      callbacks: [],
      options: new MessageSecurityOptions { AllowAnonymous = true }
    );
    services.AddSingleton<IMessageSecurityContextProvider>(provider);
    var capturingAccessor = new CapturingScopeContextAccessor();
    services.AddSingleton<IScopeContextAccessor>(capturingAccessor);
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var sp = services.BuildServiceProvider();

    // Act
    await SecurityContextHelper.EstablishFullContextAsync(envelope, sp, CancellationToken.None);

    // Assert - ImmutableScopeContext should have roles and permissions from envelope
    await Assert.That(capturingAccessor.CapturedContext).IsNotNull();
    await Assert.That(capturingAccessor.CapturedContext).IsTypeOf<ImmutableScopeContext>();
    await Assert.That(capturingAccessor.CapturedContext!.Roles).Contains("Admin");
    await Assert.That(capturingAccessor.CapturedContext!.Roles).Contains("Editor");
    await Assert.That(capturingAccessor.CapturedContext!.Permissions.Count).IsEqualTo(2);
  }

  [Test]
  public async Task Helper_EstablishFullContextAsync_WithExtractor_ScopeContextUsedForMessageContextAsync() {
    // Arrange: Extractor succeeds - securityContext is used for message context (not envelope)
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { TenantId = "extractor-tenant", UserId = "extractor-user" },
      Roles = new HashSet<string> { "Admin" },
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Extractor"
    };

    var envelope = _createHelperEnvelope(new CoverageTestMessage("test"));
    var services = new ServiceCollection();
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [new TestExtractorWithResult(100, extraction)],
      callbacks: [],
      options: new MessageSecurityOptions { AllowAnonymous = true }
    );
    services.AddSingleton<IMessageSecurityContextProvider>(provider);
    var capturingAccessor = new CapturingScopeContextAccessor();
    services.AddSingleton<IScopeContextAccessor>(capturingAccessor);
    var capturingMessageAccessor = new CapturingMessageContextAccessor();
    services.AddSingleton<IMessageContextAccessor>(capturingMessageAccessor);
    var sp = services.BuildServiceProvider();

    // Act
    await SecurityContextHelper.EstablishFullContextAsync(envelope, sp, CancellationToken.None);

    // Assert - message context should use extractor's values
    await Assert.That(capturingMessageAccessor.CapturedContext).IsNotNull();
    await Assert.That(capturingMessageAccessor.CapturedContext!.UserId).IsEqualTo("extractor-user");
    await Assert.That(capturingMessageAccessor.CapturedContext!.TenantId).IsEqualTo("extractor-tenant");
  }

  // ========================================
  // SecurityContextHelper - SetMessageContextFromEnvelope CorrelationId/CausationId
  // ========================================

  [Test]
  public async Task Helper_SetMessageContext_NoCorrelationId_GeneratesNewOneAsync() {
    // Arrange: Envelope without correlation ID in hops - covers the null-coalescing at line 100
    var envelope = new MessageEnvelope<CoverageTestMessage> {
      MessageId = MessageId.New(),
      Payload = new CoverageTestMessage("test"),
      Hops = [new MessageHop {
        Type = HopType.Current,
        ServiceInstance = _createServiceInstance(),
        Timestamp = DateTimeOffset.UtcNow,
        CorrelationId = null,
        CausationId = null,
        Scope = null
      }]
    };

    var messageAccessor = new MessageContextAccessor();
    var services = new ServiceCollection();
    services.AddSingleton<IMessageContextAccessor>(messageAccessor);
    var sp = services.BuildServiceProvider();

    // Act
    SecurityContextHelper.SetMessageContextFromEnvelope(envelope, sp);

    // Assert - should have auto-generated CorrelationId and CausationId
    await Assert.That(messageAccessor.Current).IsNotNull();
    await Assert.That(messageAccessor.Current!.CorrelationId).IsNotDefault();
    await Assert.That(messageAccessor.Current!.CausationId).IsNotDefault();
  }

  // ========================================
  // Helper Methods
  // ========================================

  private static ServiceInstanceInfo _createServiceInstance(string serviceName = "test-service") => new() {
    ServiceName = serviceName,
    InstanceId = Guid.NewGuid(),
    HostName = "test-host",
    ProcessId = 1234
  };

  private static MessageEnvelope<CoverageTestMessage> _createEnvelopeWithSecurityContext(
      SecurityContext securityContext) {
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance(),
      Timestamp = DateTimeOffset.UtcNow,
      Scope = ScopeDelta.FromSecurityContext(securityContext)
    };

    return new MessageEnvelope<CoverageTestMessage> {
      MessageId = MessageId.New(),
      Payload = new CoverageTestMessage("test-payload"),
      Hops = [hop]
    };
  }

  private static MessageEnvelope<TMessage> _createHelperEnvelope<TMessage>(
      TMessage payload) where TMessage : notnull {
    return new MessageEnvelope<TMessage> {
      MessageId = MessageId.New(),
      Payload = payload,
      Hops = [new MessageHop {
        ServiceInstance = _createServiceInstance(),
        Timestamp = DateTimeOffset.UtcNow,
        Topic = "test-topic"
      }]
    };
  }

  private static MessageEnvelope<CoverageTestMessage> _createHelperEnvelopeWithScope(
      string userId, string tenantId) {
    return new MessageEnvelope<CoverageTestMessage> {
      MessageId = MessageId.New(),
      Payload = new CoverageTestMessage("test"),
      Hops = [new MessageHop {
        ServiceInstance = _createServiceInstance(),
        Timestamp = DateTimeOffset.UtcNow,
        Topic = "test-topic",
        Scope = ScopeDelta.FromSecurityContext(new SecurityContext {
          UserId = userId,
          TenantId = tenantId
        })
      }]
    };
  }

  // ========================================
  // Test Doubles
  // ========================================

  private sealed record CoverageTestMessage(string Value);

  private sealed class NullTestExtractor : ISecurityContextExtractor {
    public int Priority => 100;

    public ValueTask<SecurityExtraction?> ExtractAsync(
        IMessageEnvelope envelope,
        MessageSecurityOptions options,
        CancellationToken cancellationToken = default) {
      return ValueTask.FromResult<SecurityExtraction?>(null);
    }
  }

  private sealed class TestExtractorWithResult(int priority, SecurityExtraction? extraction) : ISecurityContextExtractor {
    private readonly int _priority = priority;
    private readonly SecurityExtraction? _extraction = extraction;

    public int Priority => _priority;

    public ValueTask<SecurityExtraction?> ExtractAsync(
        IMessageEnvelope envelope,
        MessageSecurityOptions options,
        CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(_extraction);
    }
  }

  private sealed class TestSecurityCallback(
      Func<IScopeContext, IMessageEnvelope, IServiceProvider, CancellationToken, ValueTask> callback) : ISecurityContextCallback {
    private readonly Func<IScopeContext, IMessageEnvelope, IServiceProvider, CancellationToken, ValueTask> _callback = callback;

    public ValueTask OnContextEstablishedAsync(
        IScopeContext context,
        IMessageEnvelope envelope,
        IServiceProvider scopedProvider,
        CancellationToken cancellationToken = default) {
      return _callback(context, envelope, scopedProvider, cancellationToken);
    }
  }

  private sealed class CapturingScopeContextAccessor : IScopeContextAccessor {
    public IScopeContext? CapturedContext { get; private set; }

    public IScopeContext? Current {
      get => ScopeContextAccessor.CurrentContext;
      set {
        CapturedContext = value;
        ScopeContextAccessor.CurrentContext = value;
      }
    }

    public IMessageContext? InitiatingContext {
      get => ScopeContextAccessor.CurrentInitiatingContext;
      set => ScopeContextAccessor.CurrentInitiatingContext = value;
    }
  }

  private sealed class CapturingMessageContextAccessor : IMessageContextAccessor {
    public IMessageContext? CapturedContext { get; private set; }

    public IMessageContext? Current {
      get => MessageContextAccessor.CurrentContext;
      set {
        CapturedContext = value;
        MessageContextAccessor.CurrentContext = value;
      }
    }
  }

  private sealed class MockScopeContextAccessor(IScopeContext? explicitContext) : IScopeContextAccessor {
    private readonly IScopeContext? _explicitContext = explicitContext;

    public IScopeContext? Current {
      get => _explicitContext;
      set { /* Ignore sets - simulating explicit context */ }
    }

    public IMessageContext? InitiatingContext {
      get => ScopeContextAccessor.CurrentInitiatingContext;
      set => ScopeContextAccessor.CurrentInitiatingContext = value;
    }
  }
}
