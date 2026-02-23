using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Tests for <see cref="AwaitPerspectiveSyncAttribute"/>.
/// </summary>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
public class AwaitPerspectiveSyncAttributeTests {
  // Dummy perspective type for testing
  private sealed class TestPerspective { }
  private sealed class TestEvent { }

  // ==========================================================================
  // Constructor tests
  // ==========================================================================

  [Test]
  public async Task AwaitPerspectiveSyncAttribute_Constructor_StoresPerspectiveTypeAsync() {
    var attr = new AwaitPerspectiveSyncAttribute(typeof(TestPerspective));

    await Assert.That(attr.PerspectiveType).IsEqualTo(typeof(TestPerspective));
  }

  [Test]
  public async Task AwaitPerspectiveSyncAttribute_EventTypes_DefaultsToNullAsync() {
    var attr = new AwaitPerspectiveSyncAttribute(typeof(TestPerspective));

    await Assert.That(attr.EventTypes).IsNull();
  }

  [Test]
  public async Task AwaitPerspectiveSyncAttribute_LookupMode_DefaultsToLocalAsync() {
    var attr = new AwaitPerspectiveSyncAttribute(typeof(TestPerspective));

    await Assert.That(attr.LookupMode).IsEqualTo(SyncLookupMode.Local);
  }

  [Test]
  public async Task AwaitPerspectiveSyncAttribute_TimeoutMs_DefaultsTo5000Async() {
    var attr = new AwaitPerspectiveSyncAttribute(typeof(TestPerspective));

    await Assert.That(attr.TimeoutMs).IsEqualTo(5000);
  }

  [Test]
  public async Task AwaitPerspectiveSyncAttribute_ThrowOnTimeout_DefaultsToFalseAsync() {
    var attr = new AwaitPerspectiveSyncAttribute(typeof(TestPerspective));

    await Assert.That(attr.ThrowOnTimeout).IsFalse();
  }

  // ==========================================================================
  // Property setter tests
  // ==========================================================================

  [Test]
  public async Task AwaitPerspectiveSyncAttribute_CanSetEventTypesAsync() {
    var attr = new AwaitPerspectiveSyncAttribute(typeof(TestPerspective)) {
      EventTypes = [typeof(TestEvent)]
    };

    await Assert.That(attr.EventTypes).IsNotNull();
    await Assert.That(attr.EventTypes!.Length).IsEqualTo(1);
    await Assert.That(attr.EventTypes[0]).IsEqualTo(typeof(TestEvent));
  }

  [Test]
  public async Task AwaitPerspectiveSyncAttribute_CanSetLookupModeAsync() {
    var attr = new AwaitPerspectiveSyncAttribute(typeof(TestPerspective)) {
      LookupMode = SyncLookupMode.Distributed
    };

    await Assert.That(attr.LookupMode).IsEqualTo(SyncLookupMode.Distributed);
  }

  [Test]
  public async Task AwaitPerspectiveSyncAttribute_CanSetTimeoutMsAsync() {
    var attr = new AwaitPerspectiveSyncAttribute(typeof(TestPerspective)) {
      TimeoutMs = 10000
    };

    await Assert.That(attr.TimeoutMs).IsEqualTo(10000);
  }

  [Test]
  public async Task AwaitPerspectiveSyncAttribute_CanSetThrowOnTimeoutAsync() {
    var attr = new AwaitPerspectiveSyncAttribute(typeof(TestPerspective)) {
      ThrowOnTimeout = true
    };

    await Assert.That(attr.ThrowOnTimeout).IsTrue();
  }

  // ==========================================================================
  // Attribute usage tests
  // ==========================================================================

  [Test]
  public async Task AwaitPerspectiveSyncAttribute_AllowsMultipleOnClassAsync() {
    var usageAttr = typeof(AwaitPerspectiveSyncAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .OfType<AttributeUsageAttribute>()
        .FirstOrDefault();

    await Assert.That(usageAttr).IsNotNull();
    await Assert.That(usageAttr!.AllowMultiple).IsTrue();
  }

  [Test]
  public async Task AwaitPerspectiveSyncAttribute_TargetsClassesAsync() {
    var usageAttr = typeof(AwaitPerspectiveSyncAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .OfType<AttributeUsageAttribute>()
        .FirstOrDefault();

    await Assert.That(usageAttr).IsNotNull();
    await Assert.That(usageAttr!.ValidOn.HasFlag(AttributeTargets.Class)).IsTrue();
  }

  // ==========================================================================
  // ToSyncOptions tests
  // ==========================================================================

  [Test]
  public async Task AwaitPerspectiveSyncAttribute_ToSyncOptions_ReturnsValidOptionsAsync() {
    var attr = new AwaitPerspectiveSyncAttribute(typeof(TestPerspective)) {
      EventTypes = [typeof(TestEvent)],
      LookupMode = SyncLookupMode.Distributed,
      TimeoutMs = 10000
    };

    var options = attr.ToSyncOptions();

    await Assert.That(options).IsNotNull();
    await Assert.That(options.LookupMode).IsEqualTo(SyncLookupMode.Distributed);
    await Assert.That(options.Timeout).IsEqualTo(TimeSpan.FromMilliseconds(10000));
  }

  [Test]
  public async Task AwaitPerspectiveSyncAttribute_ToSyncOptions_WithEventTypes_CreatesEventTypeFilterAsync() {
    var attr = new AwaitPerspectiveSyncAttribute(typeof(TestPerspective)) {
      EventTypes = [typeof(TestEvent), typeof(string)]
    };

    var options = attr.ToSyncOptions();

    await Assert.That(options.Filter).IsTypeOf<EventTypeFilter>();
    var filter = (EventTypeFilter)options.Filter;
    await Assert.That(filter.EventTypes).Contains(typeof(TestEvent));
    await Assert.That(filter.EventTypes).Contains(typeof(string));
  }

  [Test]
  public async Task AwaitPerspectiveSyncAttribute_ToSyncOptions_WithoutEventTypes_CreatesAllPendingFilterAsync() {
    var attr = new AwaitPerspectiveSyncAttribute(typeof(TestPerspective));

    var options = attr.ToSyncOptions();

    // Without event types specified, should wait for all pending events
    await Assert.That(options.Filter).IsTypeOf<AllPendingFilter>();
  }
}
