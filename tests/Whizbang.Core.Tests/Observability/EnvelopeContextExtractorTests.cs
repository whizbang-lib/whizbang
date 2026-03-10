using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Tests for EnvelopeContextExtractor - shared helper for extracting
/// both trace context (TraceParent) and scope (ScopeDelta) from message hops.
/// </summary>
/// <docs>core-concepts/message-context-extraction</docs>
[Category("Core")]
[Category("Observability")]
public class EnvelopeContextExtractorTests {

  [Test]
  public async Task ExtractFromHops_WhenHopsNull_ReturnsDefaultContextAsync() {
    // Act
    var result = EnvelopeContextExtractor.ExtractFromHops(null);

    // Assert
    await Assert.That(result.TraceContext).IsEqualTo(default(ActivityContext));
    await Assert.That(result.Scope).IsNull();
  }

  [Test]
  public async Task ExtractFromHops_WhenHopsEmpty_ReturnsDefaultContextAsync() {
    // Arrange
    var hops = new List<MessageHop>();

    // Act
    var result = EnvelopeContextExtractor.ExtractFromHops(hops);

    // Assert
    await Assert.That(result.TraceContext).IsEqualTo(default(ActivityContext));
    await Assert.That(result.Scope).IsNull();
  }

  [Test]
  public async Task ExtractTraceContext_WhenTraceParentExists_ReturnsActivityContextAsync() {
    // Arrange - Valid W3C traceparent format
    var traceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
    var hops = new List<MessageHop> {
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        TraceParent = traceParent
      }
    };

    // Act
    var result = EnvelopeContextExtractor.ExtractTraceContext(hops);

    // Assert
    await Assert.That(result).IsNotEqualTo(default(ActivityContext));
    await Assert.That(result.TraceId.ToString()).IsEqualTo("0af7651916cd43dd8448eb211c80319c");
    await Assert.That(result.SpanId.ToString()).IsEqualTo("b7ad6b7169203331");
  }

  [Test]
  public async Task ExtractTraceContext_WhenNoTraceParent_ReturnsDefaultAsync() {
    // Arrange
    var hops = new List<MessageHop> {
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        TraceParent = null
      }
    };

    // Act
    var result = EnvelopeContextExtractor.ExtractTraceContext(hops);

    // Assert
    await Assert.That(result).IsEqualTo(default(ActivityContext));
  }

  [Test]
  public async Task ExtractTraceContext_WhenMultipleHops_UsesLastTraceParentAsync() {
    // Arrange
    var firstTraceParent = "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1-bbbbbbbbbbbbbb01-01";
    var lastTraceParent = "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa2-bbbbbbbbbbbbbb02-01";

    var hops = new List<MessageHop> {
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        TraceParent = firstTraceParent
      },
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        TraceParent = lastTraceParent
      }
    };

    // Act
    var result = EnvelopeContextExtractor.ExtractTraceContext(hops);

    // Assert - Should use the LAST trace parent
    await Assert.That(result.TraceId.ToString()).IsEqualTo("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa2");
  }

  [Test]
  public async Task ExtractScope_WhenScopeDeltaExists_ReturnsMergedScopeAsync() {
    // Arrange - Use FromSecurityContext to create proper ScopeDelta
    var scopeDelta = ScopeDelta.FromSecurityContext(
      new SecurityContext { TenantId = "tenant-123", UserId = "user-456" });

    var hops = new List<MessageHop> {
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Scope = scopeDelta
      }
    };

    // Act
    var result = EnvelopeContextExtractor.ExtractScope(hops);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope?.TenantId).IsEqualTo("tenant-123");
    await Assert.That(result.Scope?.UserId).IsEqualTo("user-456");
  }

  [Test]
  public async Task ExtractScope_WhenNoScopeDelta_ReturnsNullAsync() {
    // Arrange
    var hops = new List<MessageHop> {
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Scope = null
      }
    };

    // Act
    var result = EnvelopeContextExtractor.ExtractScope(hops);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ExtractScope_WhenMultipleHops_MergesScopeDeltasAsync() {
    // Arrange - First hop has TenantId, second adds UserId
    var hops = new List<MessageHop> {
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Scope = ScopeDelta.FromSecurityContext(
          new SecurityContext { TenantId = "tenant-A" })
      },
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Scope = ScopeDelta.FromSecurityContext(
          new SecurityContext { TenantId = "tenant-A", UserId = "user-B" })
      }
    };

    // Act
    var result = EnvelopeContextExtractor.ExtractScope(hops);

    // Assert - Should have BOTH TenantId and UserId from merged scope
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope?.TenantId).IsEqualTo("tenant-A");
    await Assert.That(result.Scope?.UserId).IsEqualTo("user-B");
  }

  [Test]
  public async Task ExtractScope_WhenHopTypeNotCurrent_IgnoresHopAsync() {
    // Arrange - Causation hop should be ignored, only Current hops count
    var hops = new List<MessageHop> {
      new MessageHop {
        Type = HopType.Causation,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Scope = ScopeDelta.FromSecurityContext(
          new SecurityContext { TenantId = "causation-tenant" })
      },
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Scope = ScopeDelta.FromSecurityContext(
          new SecurityContext { TenantId = "current-tenant" })
      }
    };

    // Act
    var result = EnvelopeContextExtractor.ExtractScope(hops);

    // Assert - Should only use Current hop's scope
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope?.TenantId).IsEqualTo("current-tenant");
  }

  [Test]
  public async Task ExtractFromHops_ExtractsBothTraceAndScopeAsync() {
    // Arrange - Hop with both TraceParent and ScopeDelta
    var traceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
    var scopeDelta = ScopeDelta.FromSecurityContext(
      new SecurityContext { TenantId = "combined-tenant", UserId = "combined-user" });

    var hops = new List<MessageHop> {
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        TraceParent = traceParent,
        Scope = scopeDelta
      }
    };

    // Act
    var result = EnvelopeContextExtractor.ExtractFromHops(hops);

    // Assert - Both trace context AND scope extracted
    await Assert.That(result.TraceContext).IsNotEqualTo(default(ActivityContext));
    await Assert.That(result.TraceContext.TraceId.ToString()).IsEqualTo("0af7651916cd43dd8448eb211c80319c");
    await Assert.That(result.Scope).IsNotNull();
    await Assert.That(result.Scope!.Scope?.TenantId).IsEqualTo("combined-tenant");
    await Assert.That(result.Scope.Scope?.UserId).IsEqualTo("combined-user");
  }

  [Test]
  public async Task ExtractFromEnvelope_DelegatesToExtractFromHopsAsync() {
    // Arrange
    var traceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
    var scopeDelta = ScopeDelta.FromSecurityContext(
      new SecurityContext { TenantId = "envelope-tenant" });

    var hops = new List<MessageHop> {
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        TraceParent = traceParent,
        Scope = scopeDelta
      }
    };

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = Whizbang.Core.ValueObjects.MessageId.New(),
      Payload = new TestMessage(),
      Hops = hops
    };

    // Act
    var result = EnvelopeContextExtractor.ExtractFromEnvelope(envelope);

    // Assert
    await Assert.That(result.TraceContext.TraceId.ToString()).IsEqualTo("0af7651916cd43dd8448eb211c80319c");
    await Assert.That(result.Scope).IsNotNull();
    await Assert.That(result.Scope!.Scope?.TenantId).IsEqualTo("envelope-tenant");
  }

  [Test]
  public void ExtractFromEnvelope_WhenEnvelopeNull_ThrowsArgumentNullException() {
    // Act & Assert
    Assert.Throws<ArgumentNullException>(() => EnvelopeContextExtractor.ExtractFromEnvelope(null!));
  }

  [Test]
  public async Task ExtractScope_ReturnsImmutableScopeContextAsync() {
    // Arrange
    var scopeDelta = ScopeDelta.FromSecurityContext(
      new SecurityContext { TenantId = "test-tenant" });

    var hops = new List<MessageHop> {
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Scope = scopeDelta
      }
    };

    // Act
    var result = EnvelopeContextExtractor.ExtractScope(hops);

    // Assert - Should return ImmutableScopeContext
    await Assert.That(result).IsTypeOf<ImmutableScopeContext>();
  }

  private sealed record TestMessage;
}
