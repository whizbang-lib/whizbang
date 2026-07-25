using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.AutoPopulate;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.AutoPopulate;

/// <summary>
/// A public class message so the generator emits a populator for it (records were previously the only
/// types populated). A consumer's events are all classes deriving from a base class, so this is the shape that broke.
/// </summary>
public class ClassAutoPopulateEvent {
  public Guid Id { get; set; }

  [PopulateTimestamp(TimestampKind.SentAt)]
  public DateTimeOffset? SentAt { get; set; }

  [PopulateFromIdentifier(IdentifierKind.CorrelationId)]
  public string? CorrelationId { get; set; }
}

/// <summary>
/// A class that INHERITS its auto-populate properties from <see cref="ClassAutoPopulateEvent"/> (a consumer's shape:
/// concrete events derive from a base that declares the attributes). No populator case is emitted for it —
/// the base type's case populates it in place.
/// </summary>
public class DerivedAutoPopulateEvent : ClassAutoPopulateEvent {
  public int Extra { get; set; }
}

/// <summary>
/// Regression lock for the framework keystone: AutoPopulate must set properties on CLASS messages, not just
/// records. Before the fix the generated populator was records-only, so a class event's <c>CorrelationId</c>
/// (and every other <c>[PopulateFrom*]</c> property) stayed null — the root cause of the hung activation toast.
/// Also proves the Guid→string conversion for a string CorrelationId populated from the framework's Guid id.
/// </summary>
[Category("Core")]
[Category("AutoPopulate")]
public class AutoPopulateClassPopulationTests {

  [Test]
  public async Task PopulateSent_ClassMessage_SetsPropertiesInPlaceAsync() {
    var message = new ClassAutoPopulateEvent { Id = Guid.NewGuid() };
    var correlation = CorrelationId.New();
    var timestamp = DateTimeOffset.UtcNow;
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "localhost",
        ProcessId = 1234
      },
      Timestamp = timestamp,
      CorrelationId = correlation
    };

    var result = AutoPopulatePopulatorRegistry.PopulateSent(message, hop, MessageId.New());

    var populated = (ClassAutoPopulateEvent)result;
    await Assert.That(populated).IsSameReferenceAs(message)
      .Because("Class messages are populated in place (mutated), returning the same instance.");
    await Assert.That(populated.SentAt).IsEqualTo(timestamp);
    await Assert.That(populated.CorrelationId).IsEqualTo(correlation.Value.ToString())
      .Because("A string CorrelationId is populated from the framework's Guid correlation id (Guid→string).");
  }

  [Test]
  public async Task PopulateSent_DerivedClass_IsPopulatedByBaseCaseAsync() {
    var message = new DerivedAutoPopulateEvent { Id = Guid.NewGuid(), Extra = 42 };
    var correlation = CorrelationId.New();
    var timestamp = DateTimeOffset.UtcNow;
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "localhost",
        ProcessId = 1234
      },
      Timestamp = timestamp,
      CorrelationId = correlation
    };

    var result = AutoPopulatePopulatorRegistry.PopulateSent(message, hop, MessageId.New());

    var populated = (DerivedAutoPopulateEvent)result;
    await Assert.That(populated).IsSameReferenceAs(message)
      .Because("The base type's case mutates the derived instance in place and returns it.");
    await Assert.That(populated.CorrelationId).IsEqualTo(correlation.Value.ToString())
      .Because("An inherited auto-populate property must be set on a derived instance via the base case.");
    await Assert.That(populated.SentAt).IsEqualTo(timestamp);
    await Assert.That(populated.Extra).IsEqualTo(42);
  }
}
