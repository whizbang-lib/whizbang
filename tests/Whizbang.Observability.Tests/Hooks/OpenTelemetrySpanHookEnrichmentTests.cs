using System.Diagnostics;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;
using Whizbang.Observability.Hooks;

namespace Whizbang.Observability.Tests.Hooks;

/// <summary>
/// Enrichment tests for <see cref="OpenTelemetrySpanHook"/>.
/// Unlike <see cref="OpenTelemetrySpanHookTests"/>, every test here registers an
/// <see cref="ActivityListener"/> with AllData sampling and asserts the actual tags,
/// kind, and events recorded on the created activity: scope attribute enrichment,
/// payload attribute extraction for each JSON value kind, the non-object payload
/// guard, the unknown SpanKind fallback, and RecordAsEvent content.
/// </summary>
/// <tests>Whizbang.Observability/Hooks/OpenTelemetrySpanHook.cs</tests>
[Category("Observability")]
[Category("Hooks")]
public class OpenTelemetrySpanHookEnrichmentTests {
  private static readonly string[] _richPayloadProperties = ["Name", "Amount", "IsActive", "IsArchived", "Details", "DoesNotExist"];
  private static readonly string[] _nameOnlyProperties = ["Name"];
  private static readonly int[] _arrayPayload = [1, 2, 3];

  [Test]
  public async Task OnTaggedMessage_WithFullScope_SetsAllScopeAndStandardTagsAsync() {
    // Arrange
    using var capture = new ActivityCapture();
    var hook = new OpenTelemetrySpanHook();
    var attribute = new TelemetryTagAttribute { Tag = "enrich-full-scope" };
    var message = new TestEvent { Id = Guid.NewGuid(), Name = "Test" };
    var scope = new ScopeContext {
      Scope = new PerspectiveScope {
        TenantId = "tenant-1",
        UserId = "user-2",
        CustomerId = "customer-3",
        OrganizationId = "org-4"
      },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };
    var context = new TagContext<TelemetryTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestEvent),
      Payload = JsonSerializer.SerializeToElement(message),
      Scope = scope
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert - all four scope attributes are recorded on the span
    await Assert.That(result).IsNull();
    var activity = capture.Single("enrich-full-scope");
    await Assert.That((string?)activity.GetTagItem("whizbang.scope.tenantid")).IsEqualTo("tenant-1");
    await Assert.That((string?)activity.GetTagItem("whizbang.scope.userid")).IsEqualTo("user-2");
    await Assert.That((string?)activity.GetTagItem("whizbang.scope.customerid")).IsEqualTo("customer-3");
    await Assert.That((string?)activity.GetTagItem("whizbang.scope.organizationid")).IsEqualTo("org-4");

    // Standard messaging tags are also present
    await Assert.That((string?)activity.GetTagItem("messaging.system")).IsEqualTo("whizbang");
    await Assert.That((string?)activity.GetTagItem("messaging.operation")).IsEqualTo("process");
    await Assert.That((string?)activity.GetTagItem("whizbang.tag")).IsEqualTo("enrich-full-scope");
    await Assert.That((string?)activity.GetTagItem("whizbang.message_type")).IsEqualTo(typeof(TestEvent).FullName);
  }

  [Test]
  public async Task OnTaggedMessage_WithPartialScope_OmitsNullAndEmptyScopeTagsAsync() {
    // Arrange - only TenantId has a value; UserId/OrganizationId null, CustomerId empty
    using var capture = new ActivityCapture();
    var hook = new OpenTelemetrySpanHook();
    var attribute = new TelemetryTagAttribute { Tag = "enrich-partial-scope" };
    var message = new TestEvent { Id = Guid.NewGuid(), Name = "Test" };
    var scope = new ScopeContext {
      Scope = new PerspectiveScope {
        TenantId = "tenant-only",
        CustomerId = ""
      },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };
    var context = new TagContext<TelemetryTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestEvent),
      Payload = JsonSerializer.SerializeToElement(message),
      Scope = scope
    };

    // Act
    await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert - only the populated scope value becomes a tag
    var activity = capture.Single("enrich-partial-scope");
    await Assert.That((string?)activity.GetTagItem("whizbang.scope.tenantid")).IsEqualTo("tenant-only");
    await Assert.That(activity.GetTagItem("whizbang.scope.userid")).IsNull();
    await Assert.That(activity.GetTagItem("whizbang.scope.customerid")).IsNull();
    await Assert.That(activity.GetTagItem("whizbang.scope.organizationid")).IsNull();
  }

  [Test]
  public async Task OnTaggedMessage_WithoutScope_SetsNoScopeTagsAsync() {
    // Arrange - context.Scope is null entirely
    using var capture = new ActivityCapture();
    var hook = new OpenTelemetrySpanHook();
    var attribute = new TelemetryTagAttribute { Tag = "enrich-no-scope" };
    var message = new TestEvent { Id = Guid.NewGuid(), Name = "Test" };
    var context = new TagContext<TelemetryTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestEvent),
      Payload = JsonSerializer.SerializeToElement(message)
    };

    // Act
    await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert - standard tags set, scope tags absent
    var activity = capture.Single("enrich-no-scope");
    await Assert.That((string?)activity.GetTagItem("messaging.system")).IsEqualTo("whizbang");
    await Assert.That(activity.GetTagItem("whizbang.scope.tenantid")).IsNull();
    await Assert.That(activity.GetTagItem("whizbang.scope.userid")).IsNull();
  }

  [Test]
  public async Task OnTaggedMessage_WithProperties_AddsPayloadTagsForEachValueKindAsync() {
    // Arrange - payload covers String, Number, True, False, and Object (default arm)
    using var capture = new ActivityCapture();
    var hook = new OpenTelemetrySpanHook();
    var attribute = new TelemetryTagAttribute {
      Tag = "enrich-payload-kinds",
      Properties = _richPayloadProperties
    };
    var message = new RichEvent {
      Id = Guid.NewGuid(),
      Name = "OrderName",
      Amount = 42,
      IsActive = true,
      IsArchived = false,
      Details = new NestedDetails { X = 7 }
    };
    var context = new TagContext<TelemetryTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(RichEvent),
      Payload = JsonSerializer.SerializeToElement(message)
    };

    // Act
    await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert - one tag per matched property, lowercased, converted per value kind
    var activity = capture.Single("enrich-payload-kinds");
    await Assert.That((string?)activity.GetTagItem("whizbang.payload.name")).IsEqualTo("OrderName");
    await Assert.That((string?)activity.GetTagItem("whizbang.payload.amount")).IsEqualTo("42");
    await Assert.That((string?)activity.GetTagItem("whizbang.payload.isactive")).IsEqualTo("true");
    await Assert.That((string?)activity.GetTagItem("whizbang.payload.isarchived")).IsEqualTo("false");
    await Assert.That((string?)activity.GetTagItem("whizbang.payload.details")).IsEqualTo("{\"X\":7}");

    // Property missing from the payload produces no tag
    await Assert.That(activity.GetTagItem("whizbang.payload.doesnotexist")).IsNull();
  }

  [Test]
  public async Task OnTaggedMessage_WithNonObjectPayload_AddsNoPayloadTagsAsync() {
    // Arrange - payload is a JSON array, so _addPayloadAttributes must return early
    using var capture = new ActivityCapture();
    var hook = new OpenTelemetrySpanHook();
    var attribute = new TelemetryTagAttribute {
      Tag = "enrich-array-payload",
      Properties = _nameOnlyProperties
    };
    var message = new TestEvent { Id = Guid.NewGuid(), Name = "Test" };
    var context = new TagContext<TelemetryTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestEvent),
      Payload = JsonSerializer.SerializeToElement(_arrayPayload)
    };

    // Act
    await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert - activity exists with standard tags but no payload tags
    var activity = capture.Single("enrich-array-payload");
    await Assert.That((string?)activity.GetTagItem("whizbang.tag")).IsEqualTo("enrich-array-payload");
    await Assert.That(activity.GetTagItem("whizbang.payload.name")).IsNull();
  }

  [Test]
  public async Task OnTaggedMessage_WithUnknownSpanKind_DefaultsToInternalAsync() {
    // Arrange - out-of-range SpanKind exercises the switch default arm
    using var capture = new ActivityCapture();
    var hook = new OpenTelemetrySpanHook();
    var attribute = new TelemetryTagAttribute {
      Tag = "enrich-unknown-kind",
      Kind = (SpanKind)999
    };
    var message = new TestEvent { Id = Guid.NewGuid(), Name = "Test" };
    var context = new TagContext<TelemetryTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestEvent),
      Payload = JsonSerializer.SerializeToElement(message)
    };

    // Act
    await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    var activity = capture.Single("enrich-unknown-kind");
    await Assert.That(activity.Kind).IsEqualTo(ActivityKind.Internal);
  }

  [Test]
  public async Task OnTaggedMessage_WithRecordAsEvent_AddsActivityEventWithMessageTypeNameAsync() {
    // Arrange
    using var capture = new ActivityCapture();
    var hook = new OpenTelemetrySpanHook();
    var attribute = new TelemetryTagAttribute {
      Tag = "enrich-record-event",
      RecordAsEvent = true
    };
    var message = new TestEvent { Id = Guid.NewGuid(), Name = "Test" };
    var context = new TagContext<TelemetryTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestEvent),
      Payload = JsonSerializer.SerializeToElement(message)
    };

    // Act
    await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert - the activity carries exactly one event named after the tag,
    // with the message type short name in the event tags
    var activity = capture.Single("enrich-record-event");
    var events = activity.Events.ToList();
    await Assert.That(events.Count).IsEqualTo(1);
    await Assert.That(events[0].Name).IsEqualTo("enrich-record-event");

    var eventNameTag = events[0].Tags.Single(t => t.Key == "event.name");
    await Assert.That((string?)eventNameTag.Value).IsEqualTo("TestEvent");
  }

  [Test]
  public async Task OnTaggedMessage_UsesSpanNameOverTag_ForActivityOperationNameAsync() {
    // Arrange - SpanName takes precedence over Tag when both are set
    using var capture = new ActivityCapture();
    var hook = new OpenTelemetrySpanHook();
    var attribute = new TelemetryTagAttribute {
      Tag = "enrich-tag-not-span-name",
      SpanName = "EnrichExplicitSpanName"
    };
    var message = new TestEvent { Id = Guid.NewGuid(), Name = "Test" };
    var context = new TagContext<TelemetryTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestEvent),
      Payload = JsonSerializer.SerializeToElement(message)
    };

    // Act
    await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert - operation name is the SpanName; the tag is still recorded as an attribute
    var activity = capture.Single("EnrichExplicitSpanName");
    await Assert.That((string?)activity.GetTagItem("whizbang.tag")).IsEqualTo("enrich-tag-not-span-name");
  }

  /// <summary>
  /// Registers an ActivityListener with AllData sampling for the Whizbang.MessageTags
  /// source and captures every started activity. Owns the disposable listener (CA1001),
  /// so it implements IDisposable. Captured activities are filtered by operation name
  /// because the ActivitySource is a shared static and tests run in parallel.
  /// </summary>
  private sealed class ActivityCapture : IDisposable {
    private readonly List<Activity> _started = [];
    private readonly ActivityListener _listener;

    public ActivityCapture() {
      _listener = new ActivityListener {
        ShouldListenTo = source => source.Name == "Whizbang.MessageTags",
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        ActivityStarted = activity => {
          lock (_started) {
            _started.Add(activity);
          }
        }
      };
      ActivitySource.AddActivityListener(_listener);
    }

    public Activity Single(string operationName) {
      lock (_started) {
        return _started.Single(a => a.OperationName == operationName);
      }
    }

    public void Dispose() => _listener.Dispose();
  }

  // Test event types
  private sealed record TestEvent {
    public required Guid Id { get; init; }
    public required string Name { get; init; }
  }

  private sealed record RichEvent {
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required int Amount { get; init; }
    public required bool IsActive { get; init; }
    public required bool IsArchived { get; init; }
    public required NestedDetails Details { get; init; }
  }

  private sealed record NestedDetails {
    public required int X { get; init; }
  }
}
