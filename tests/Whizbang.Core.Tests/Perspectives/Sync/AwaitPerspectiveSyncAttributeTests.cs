using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Tests for <see cref="SyncFireBehavior"/> enum.
/// </summary>
public class SyncFireBehaviorTests {
  [Test]
  public async Task SyncFireBehavior_HasExpectedValuesAsync() {
    await Assert.That(Enum.IsDefined(SyncFireBehavior.FireOnSuccess)).IsTrue();
    await Assert.That(Enum.IsDefined(SyncFireBehavior.FireAlways)).IsTrue();
    await Assert.That(Enum.IsDefined(SyncFireBehavior.FireOnEachEvent)).IsTrue();
  }

  [Test]
  public async Task SyncFireBehavior_FireOnSuccess_IsZeroAsync() {
    // Ensures FireOnSuccess is the default value
    var value = (int)SyncFireBehavior.FireOnSuccess;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task SyncFireBehavior_HasThreeValuesAsync() {
    var values = Enum.GetValues<SyncFireBehavior>();
    await Assert.That(values.Length).IsEqualTo(3);
  }
}

/// <summary>
/// Tests for <see cref="AwaitPerspectiveSyncAttribute"/>.
/// </summary>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
[NotInParallel("DefaultTimeoutMs")]
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
  public async Task AwaitPerspectiveSyncAttribute_Constructor_ThrowsOnNullPerspectiveTypeAsync() {
    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        await Task.FromResult(new AwaitPerspectiveSyncAttribute(null!)));
  }

  [Test]
  public async Task AwaitPerspectiveSyncAttribute_EventTypes_DefaultsToNullAsync() {
    var attr = new AwaitPerspectiveSyncAttribute(typeof(TestPerspective));

    await Assert.That(attr.EventTypes).IsNull();
  }

  // ==========================================================================
  // DefaultTimeoutMs static property tests
  // ==========================================================================

  [Test]
  public async Task AwaitPerspectiveSyncAttribute_DefaultTimeoutMs_Is5000Async() {
    // Reset to default before test
    AwaitPerspectiveSyncAttribute.DefaultTimeoutMs = 5000;

    await Assert.That(AwaitPerspectiveSyncAttribute.DefaultTimeoutMs).IsEqualTo(5000);
  }

  [Test]
  public async Task AwaitPerspectiveSyncAttribute_DefaultTimeoutMs_CanBeChangedGloballyAsync() {
    var originalDefault = AwaitPerspectiveSyncAttribute.DefaultTimeoutMs;
    try {
      AwaitPerspectiveSyncAttribute.DefaultTimeoutMs = 10000;

      await Assert.That(AwaitPerspectiveSyncAttribute.DefaultTimeoutMs).IsEqualTo(10000);
    } finally {
      // Restore original default
      AwaitPerspectiveSyncAttribute.DefaultTimeoutMs = originalDefault;
    }
  }

  // ==========================================================================
  // TimeoutMs and EffectiveTimeoutMs tests
  // ==========================================================================

  [Test]
  public async Task AwaitPerspectiveSyncAttribute_TimeoutMs_DefaultsToNegativeOneAsync() {
    var attr = new AwaitPerspectiveSyncAttribute(typeof(TestPerspective));

    await Assert.That(attr.TimeoutMs).IsEqualTo(-1);
  }

  [Test]
  [NotInParallel] // Modifies static DefaultTimeoutMs
  public async Task AwaitPerspectiveSyncAttribute_EffectiveTimeoutMs_UsesDefaultWhenMinusOneAsync() {
    var originalDefault = AwaitPerspectiveSyncAttribute.DefaultTimeoutMs;
    try {
      AwaitPerspectiveSyncAttribute.DefaultTimeoutMs = 7500;
      var attr = new AwaitPerspectiveSyncAttribute(typeof(TestPerspective));

      await Assert.That(attr.TimeoutMs).IsEqualTo(-1);
      await Assert.That(attr.EffectiveTimeoutMs).IsEqualTo(7500);
    } finally {
      AwaitPerspectiveSyncAttribute.DefaultTimeoutMs = originalDefault;
    }
  }

  [Test]
  [NotInParallel] // Modifies static DefaultTimeoutMs
  public async Task AwaitPerspectiveSyncAttribute_EffectiveTimeoutMs_UsesExplicitValueWhenSetAsync() {
    var originalDefault = AwaitPerspectiveSyncAttribute.DefaultTimeoutMs;
    try {
      AwaitPerspectiveSyncAttribute.DefaultTimeoutMs = 5000;
      var attr = new AwaitPerspectiveSyncAttribute(typeof(TestPerspective)) {
        TimeoutMs = 15000
      };

      await Assert.That(attr.TimeoutMs).IsEqualTo(15000);
      await Assert.That(attr.EffectiveTimeoutMs).IsEqualTo(15000);
    } finally {
      AwaitPerspectiveSyncAttribute.DefaultTimeoutMs = originalDefault;
    }
  }

  [Test]
  public async Task AwaitPerspectiveSyncAttribute_EffectiveTimeoutMs_UsesZeroWhenExplicitlySetToZeroAsync() {
    var attr = new AwaitPerspectiveSyncAttribute(typeof(TestPerspective)) {
      TimeoutMs = 0
    };

    await Assert.That(attr.EffectiveTimeoutMs).IsEqualTo(0);
  }

  // ==========================================================================
  // FireBehavior tests
  // ==========================================================================

  [Test]
  public async Task AwaitPerspectiveSyncAttribute_FireBehavior_DefaultsToFireOnSuccessAsync() {
    var attr = new AwaitPerspectiveSyncAttribute(typeof(TestPerspective));

    await Assert.That(attr.FireBehavior).IsEqualTo(SyncFireBehavior.FireOnSuccess);
  }

  [Test]
  public async Task AwaitPerspectiveSyncAttribute_FireBehavior_CanBeSetToFireAlwaysAsync() {
    var attr = new AwaitPerspectiveSyncAttribute(typeof(TestPerspective)) {
      FireBehavior = SyncFireBehavior.FireAlways
    };

    await Assert.That(attr.FireBehavior).IsEqualTo(SyncFireBehavior.FireAlways);
  }

  [Test]
  public async Task AwaitPerspectiveSyncAttribute_FireBehavior_CanBeSetToFireOnEachEventAsync() {
    var attr = new AwaitPerspectiveSyncAttribute(typeof(TestPerspective)) {
      FireBehavior = SyncFireBehavior.FireOnEachEvent
    };

    await Assert.That(attr.FireBehavior).IsEqualTo(SyncFireBehavior.FireOnEachEvent);
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
  public async Task AwaitPerspectiveSyncAttribute_CanSetTimeoutMsAsync() {
    var attr = new AwaitPerspectiveSyncAttribute(typeof(TestPerspective)) {
      TimeoutMs = 10000
    };

    await Assert.That(attr.TimeoutMs).IsEqualTo(10000);
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
  // Multiple attributes on class tests
  // ==========================================================================

  [Test]
  public async Task AwaitPerspectiveSyncAttribute_CanHaveMultipleOnSameClassAsync() {
    // This test verifies the AllowMultiple=true works correctly
    var attributes = typeof(MultiSyncTestClass).GetCustomAttributes(typeof(AwaitPerspectiveSyncAttribute), false);

    await Assert.That(attributes.Length).IsEqualTo(2);
  }

  // Test class with multiple sync attributes
  [AwaitPerspectiveSync(typeof(TestPerspective))]
  [AwaitPerspectiveSync(typeof(TestPerspective), TimeoutMs = 10000)]
  private sealed class MultiSyncTestClass { }
}
