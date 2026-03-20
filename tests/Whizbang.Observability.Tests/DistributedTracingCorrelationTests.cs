using System.Diagnostics;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Testing.Observability;

namespace Whizbang.Observability.Tests;

/// <summary>
/// Tests for distributed tracing correlation across service boundaries.
/// Validates that spans created from incoming messages (via transport or perspective processing)
/// are properly linked to their parent trace context extracted from message hops.
/// </summary>
/// <remarks>
/// These tests ensure that:
/// <list type="bullet">
/// <item>TransportConsumerWorker extracts TraceParent from incoming message hops</item>
/// <item>PerspectiveWorker extracts TraceParent from the first event's hops</item>
/// <item>Child spans are properly parented to the extracted trace context</item>
/// </list>
/// </remarks>
[NotInParallel(Order = 2)]
public class DistributedTracingCorrelationTests {

  [Test]
  public async Task TraceParentExtraction_FromMessageHops_LinksSpansCorrectlyAsync() {
    // Arrange - Simulate a message arriving from another service with trace context
    using var collector = new InMemorySpanCollector("Whizbang.Transport", "Whizbang.Tracing");

    // Simulate the sender's trace context
    using var senderActivity = WhizbangActivitySource.Tracing.StartActivity("Sender Request", ActivityKind.Server);
    var senderTraceParent = senderActivity?.Id;

    // Create a message envelope with the sender's trace context in hops
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "sender-service",
        HostName = "sender-host",
        ProcessId = 1234,
        InstanceId = Guid.NewGuid()
      },
      Timestamp = DateTimeOffset.UtcNow,
      TraceParent = senderTraceParent
    };

    // Act - Extract trace parent and create child activity (simulates what TransportConsumerWorker does)
    ActivityContext extractedContext = default;
    var traceParent = hop.TraceParent;
    if (traceParent is not null && ActivityContext.TryParse(traceParent, null, out var parsedContext)) {
      extractedContext = parsedContext;
    }

    using var inboxActivity = WhizbangActivitySource.Transport.StartActivity(
      "Inbox TestMessage",
      ActivityKind.Consumer,
      parentContext: extractedContext
    );

    // Assert - The inbox activity should be a child of the sender activity
    await Assert.That(inboxActivity).IsNotNull();
    await Assert.That(inboxActivity!.TraceId).IsEqualTo(senderActivity!.TraceId);
    await Assert.That(inboxActivity.ParentSpanId).IsEqualTo(senderActivity.SpanId);
  }

  [Test]
  public async Task TraceParentExtraction_FromEventEnvelopeHops_LinksSpansCorrectlyAsync() {
    // Arrange - Simulate perspective processing with events that have trace context
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");

    // Simulate the original request's trace context (e.g., from BFF)
    using var originalRequestActivity = WhizbangActivitySource.Tracing.StartActivity("BFF Request", ActivityKind.Server);
    var originalTraceParent = originalRequestActivity?.Id;

    // Create message hops that would be on an event envelope in the event store
    // The hops are from when the event was created during the original request
    var eventHops = new List<MessageHop> {
      new() {
        Type = HopType.Current,
        ServiceInstance = new ServiceInstanceInfo {
          ServiceName = "bff-service",
          HostName = "bff-host",
          ProcessId = 5678,
          InstanceId = Guid.NewGuid()
        },
        Timestamp = DateTimeOffset.UtcNow,
        TraceParent = originalTraceParent
      }
    };

    // Act - Extract trace parent from the first event's hops (simulates what PerspectiveWorker does)
    var extractedTraceParent = eventHops
      .Where(h => h.Type == HopType.Current)
      .Select(h => h.TraceParent)
      .LastOrDefault(tp => tp is not null);

    ActivityContext perspectiveParentContext = default;
    if (extractedTraceParent is not null && ActivityContext.TryParse(extractedTraceParent, null, out var parsedContext)) {
      perspectiveParentContext = parsedContext;
    }

    using var perspectiveActivity = WhizbangActivitySource.Tracing.StartActivity(
      "Perspective TestProjection",
      ActivityKind.Internal,
      parentContext: perspectiveParentContext
    );

    // Assert - The perspective activity should be linked to the original request
    await Assert.That(perspectiveActivity).IsNotNull();
    await Assert.That(perspectiveActivity!.TraceId).IsEqualTo(originalRequestActivity!.TraceId);
    await Assert.That(perspectiveActivity.ParentSpanId).IsEqualTo(originalRequestActivity.SpanId);
  }

  [Test]
  public async Task TraceParentExtraction_WhenNoTraceParentInHops_CreatesOrphanedSpanAsync() {
    // Arrange - Message without trace context (e.g., from a background job without active trace)
    using var collector = new InMemorySpanCollector("Whizbang.Transport");

    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "background-job",
        HostName = "worker-host",
        ProcessId = 9999,
        InstanceId = Guid.NewGuid()
      },
      Timestamp = DateTimeOffset.UtcNow,
      TraceParent = null // No trace context
    };

    // Act - Extract trace parent (should be null)
    var traceParent = hop.TraceParent;
    ActivityContext extractedContext = default;
    if (traceParent is not null && ActivityContext.TryParse(traceParent, null, out var parsedContext)) {
      extractedContext = parsedContext;
    }

    using var inboxActivity = WhizbangActivitySource.Transport.StartActivity(
      "Inbox BackgroundMessage",
      ActivityKind.Consumer,
      parentContext: extractedContext // This is default (no parent)
    );

    // Assert - Activity should be created but as a root span (orphaned)
    await Assert.That(inboxActivity).IsNotNull();
    await Assert.That(inboxActivity!.ParentSpanId).IsEqualTo(default(ActivitySpanId));
  }

  [Test]
  public async Task TraceParentExtraction_WithMultipleHops_UsesLastCurrentHopAsync() {
    // Arrange - Message that has passed through multiple services
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");

    // First hop (oldest - from initial request)
    using var firstActivity = WhizbangActivitySource.Tracing.StartActivity("First Service", ActivityKind.Server);
    var firstTraceParent = firstActivity?.Id;

    // Second hop (most recent - from intermediate service)
    using var secondActivity = WhizbangActivitySource.Tracing.StartActivity("Second Service", ActivityKind.Server);
    var secondTraceParent = secondActivity?.Id;

    var hops = new List<MessageHop> {
      new() {
        Type = HopType.Current,
        ServiceInstance = new ServiceInstanceInfo {
          ServiceName = "first-service",
          HostName = "first-host",
          ProcessId = 1111,
          InstanceId = Guid.NewGuid()
        },
        Timestamp = DateTimeOffset.UtcNow.AddSeconds(-2),
        TraceParent = firstTraceParent
      },
      new() {
        Type = HopType.Current,
        ServiceInstance = new ServiceInstanceInfo {
          ServiceName = "second-service",
          HostName = "second-host",
          ProcessId = 2222,
          InstanceId = Guid.NewGuid()
        },
        Timestamp = DateTimeOffset.UtcNow,
        TraceParent = secondTraceParent
      }
    };

    // Act - Extract the LAST non-null trace parent (most recent)
    var extractedTraceParent = hops
      .Where(h => h.Type == HopType.Current)
      .Select(h => h.TraceParent)
      .LastOrDefault(tp => tp is not null);

    ActivityContext parentContext = default;
    if (extractedTraceParent is not null && ActivityContext.TryParse(extractedTraceParent, null, out var parsedContext)) {
      parentContext = parsedContext;
    }

    using var childActivity = WhizbangActivitySource.Tracing.StartActivity(
      "Child Activity",
      ActivityKind.Internal,
      parentContext: parentContext
    );

    // Assert - Should be parented to the SECOND (most recent) activity
    await Assert.That(childActivity).IsNotNull();
    await Assert.That(childActivity!.ParentSpanId).IsEqualTo(secondActivity!.SpanId);
  }

  [Test]
  public async Task TraceTree_BuildsCorrectHierarchy_WithExtractedParentContextAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing", "Whizbang.Transport");

    // Simulate: BFF Request -> Transport -> Inbox -> Receptor -> Perspective
    using (var bffRequest = WhizbangActivitySource.Tracing.StartActivity("POST /graphql", ActivityKind.Server)) {
      // Transport sends message (hop captures current trace context)
      var traceParent = bffRequest?.Id;

      // Simulate receiver extracting trace context
      ActivityContext parentContext = default;
      if (traceParent is not null && ActivityContext.TryParse(traceParent, null, out var parsed)) {
        parentContext = parsed;
      }

      // Inbox activity parented to BFF request
      using var inbox = WhizbangActivitySource.Transport.StartActivity(
        "Inbox UserCreatedEvent",
        ActivityKind.Consumer,
        parentContext: parentContext);

      // Receptor runs under inbox
      using (WhizbangActivitySource.Tracing.StartActivity("Receptor UserCreatedHandler", ActivityKind.Internal)) { }
    }

    // Assert - Build tree and verify hierarchy
    var tree = collector.BuildTree();
    await Assert.That(tree.Span).IsNotNull();
    await Assert.That(tree.Span!.Name).IsEqualTo("POST /graphql");
    await Assert.That(tree.Children.Count).IsEqualTo(1);
    await Assert.That(tree.Children[0].Span!.Name).IsEqualTo("Inbox UserCreatedEvent");
    await Assert.That(tree.Children[0].Children.Count).IsEqualTo(1);
    await Assert.That(tree.Children[0].Children[0].Span!.Name).IsEqualTo("Receptor UserCreatedHandler");

    // Verify no orphaned spans
    await Assert.That(collector.HasOrphanedSpans()).IsFalse();
  }

  [Test]
  public async Task PerspectiveSpan_WhenEventsHaveTraceContext_IsLinkedToOriginalRequestAsync() {
    // Arrange - This simulates the PerspectiveWorker flow
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");

    Activity? perspectiveActivity = null;
    Activity? bffRequest = null;

    // Use nested using blocks so activities are completed before BuildTree()
    using (bffRequest = WhizbangActivitySource.Tracing.StartActivity("BFF GraphQL Mutation", ActivityKind.Server)) {
      var bffTraceParent = bffRequest?.Id;

      // Simulate the event envelope stored in event store (with hops from creation)
      var eventHops = new List<MessageHop> {
        new() {
          Type = HopType.Current,
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "user-service",
            HostName = "user-host",
            ProcessId = 1234,
            InstanceId = Guid.NewGuid()
          },
          Timestamp = DateTimeOffset.UtcNow,
          TraceParent = bffTraceParent
        }
      };

      // Act - PerspectiveWorker extracts trace context from first event's hops
      var perspectiveParentContext = default(ActivityContext);

      var traceParent = eventHops
        .Where(h => h.Type == HopType.Current)
        .Select(h => h.TraceParent)
        .LastOrDefault(tp => tp is not null);

      if (traceParent is not null && ActivityContext.TryParse(traceParent, null, out var extractedContext)) {
        perspectiveParentContext = extractedContext;
      }

      // Create perspective activity with extracted parent
      using (perspectiveActivity = WhizbangActivitySource.Tracing.StartActivity(
        "Perspective UserProjection",
        ActivityKind.Internal,
        parentContext: perspectiveParentContext)) {

        // Create child lifecycle activities
        using (WhizbangActivitySource.Tracing.StartActivity("Lifecycle PrePerspectiveAsync", ActivityKind.Internal)) { }
        using (WhizbangActivitySource.Tracing.StartActivity("Perspective RunAsync", ActivityKind.Internal)) { }

        // Assert - Perspective should be linked to BFF request (while activities are still open)
        await Assert.That(perspectiveActivity).IsNotNull();
        await Assert.That(perspectiveActivity!.TraceId).IsEqualTo(bffRequest!.TraceId);
        await Assert.That(perspectiveActivity.ParentSpanId).IsEqualTo(bffRequest.SpanId);
      }
    }

    // Verify tree structure (activities are now completed and collected)
    var tree = collector.BuildTree();
    tree.AssertName("BFF GraphQL Mutation")
        .AssertHasChild("Perspective UserProjection");
  }

  // Test helper class
  private sealed record TestEvent {
    public required string Name { get; init; }
  }
}
