using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tracing;

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for <see cref="TraceComponents"/> flags enum.
/// </summary>
public class TraceComponentsTests {
  // ==========================================================================
  // Default Value Tests
  // ==========================================================================

  [Test]
  public async Task None_HasValueZeroAsync() {
    var none = TraceComponents.None;
    await Assert.That((int)none).IsEqualTo(0);
  }

  // ==========================================================================
  // Individual Component Tests
  // ==========================================================================

  [Test]
  public async Task Handlers_IsFlagValueAsync() {
    var handlers = TraceComponents.Handlers;
    var none = TraceComponents.None;
    await Assert.That(handlers).IsNotEqualTo(none);
  }

  [Test]
  public async Task Lifecycle_IsFlagValueAsync() {
    var lifecycle = TraceComponents.Lifecycle;
    var none = TraceComponents.None;
    await Assert.That(lifecycle).IsNotEqualTo(none);
  }

  [Test]
  public async Task Dispatcher_IsFlagValueAsync() {
    var dispatcher = TraceComponents.Dispatcher;
    var none = TraceComponents.None;
    await Assert.That(dispatcher).IsNotEqualTo(none);
  }

  [Test]
  public async Task Messages_IsFlagValueAsync() {
    var messages = TraceComponents.Messages;
    var none = TraceComponents.None;
    await Assert.That(messages).IsNotEqualTo(none);
  }

  [Test]
  public async Task Events_IsFlagValueAsync() {
    var events = TraceComponents.Events;
    var none = TraceComponents.None;
    await Assert.That(events).IsNotEqualTo(none);
  }

  [Test]
  public async Task Outbox_IsFlagValueAsync() {
    var outbox = TraceComponents.Outbox;
    var none = TraceComponents.None;
    await Assert.That(outbox).IsNotEqualTo(none);
  }

  [Test]
  public async Task Inbox_IsFlagValueAsync() {
    var inbox = TraceComponents.Inbox;
    var none = TraceComponents.None;
    await Assert.That(inbox).IsNotEqualTo(none);
  }

  [Test]
  public async Task EventStore_IsFlagValueAsync() {
    var eventStore = TraceComponents.EventStore;
    var none = TraceComponents.None;
    await Assert.That(eventStore).IsNotEqualTo(none);
  }

  [Test]
  public async Task Perspectives_IsFlagValueAsync() {
    var perspectives = TraceComponents.Perspectives;
    var none = TraceComponents.None;
    await Assert.That(perspectives).IsNotEqualTo(none);
  }

  [Test]
  public async Task Tags_IsFlagValueAsync() {
    var tags = TraceComponents.Tags;
    var none = TraceComponents.None;
    await Assert.That(tags).IsNotEqualTo(none);
  }

  [Test]
  public async Task Security_IsFlagValueAsync() {
    var security = TraceComponents.Security;
    var none = TraceComponents.None;
    await Assert.That(security).IsNotEqualTo(none);
  }

  [Test]
  public async Task Workers_IsFlagValueAsync() {
    var workers = TraceComponents.Workers;
    var none = TraceComponents.None;
    await Assert.That(workers).IsNotEqualTo(none);
  }

  [Test]
  public async Task Errors_IsFlagValueAsync() {
    var errors = TraceComponents.Errors;
    var none = TraceComponents.None;
    await Assert.That(errors).IsNotEqualTo(none);
  }

  // ==========================================================================
  // Flags Combination Tests
  // ==========================================================================

  [Test]
  public async Task All_IncludesAllComponentsAsync() {
    var all = TraceComponents.All;

    await Assert.That(all.HasFlag(TraceComponents.Handlers)).IsTrue();
    await Assert.That(all.HasFlag(TraceComponents.Lifecycle)).IsTrue();
    await Assert.That(all.HasFlag(TraceComponents.Dispatcher)).IsTrue();
    await Assert.That(all.HasFlag(TraceComponents.Messages)).IsTrue();
    await Assert.That(all.HasFlag(TraceComponents.Events)).IsTrue();
    await Assert.That(all.HasFlag(TraceComponents.Outbox)).IsTrue();
    await Assert.That(all.HasFlag(TraceComponents.Inbox)).IsTrue();
    await Assert.That(all.HasFlag(TraceComponents.EventStore)).IsTrue();
    await Assert.That(all.HasFlag(TraceComponents.Perspectives)).IsTrue();
    await Assert.That(all.HasFlag(TraceComponents.Tags)).IsTrue();
    await Assert.That(all.HasFlag(TraceComponents.Security)).IsTrue();
    await Assert.That(all.HasFlag(TraceComponents.Workers)).IsTrue();
    await Assert.That(all.HasFlag(TraceComponents.Errors)).IsTrue();
  }

  [Test]
  public async Task CanCombineMultipleComponentsAsync() {
    var combined = TraceComponents.Handlers | TraceComponents.Lifecycle | TraceComponents.Errors;

    await Assert.That(combined.HasFlag(TraceComponents.Handlers)).IsTrue();
    await Assert.That(combined.HasFlag(TraceComponents.Lifecycle)).IsTrue();
    await Assert.That(combined.HasFlag(TraceComponents.Errors)).IsTrue();
    await Assert.That(combined.HasFlag(TraceComponents.Outbox)).IsFalse();
  }

  [Test]
  public async Task ComponentsAreDistinctFlagsAsync() {
    // Each component should be a unique power of 2
    var components = new[] {
      TraceComponents.Handlers,
      TraceComponents.Lifecycle,
      TraceComponents.Dispatcher,
      TraceComponents.Messages,
      TraceComponents.Events,
      TraceComponents.Outbox,
      TraceComponents.Inbox,
      TraceComponents.EventStore,
      TraceComponents.Perspectives,
      TraceComponents.Tags,
      TraceComponents.Security,
      TraceComponents.Workers,
      TraceComponents.Errors
    };

    // Check no two components are equal
    for (int i = 0; i < components.Length; i++) {
      for (int j = i + 1; j < components.Length; j++) {
        await Assert.That(components[i]).IsNotEqualTo(components[j]);
      }
    }
  }

  [Test]
  public async Task HasFlagsAttributeAsync() {
    var type = typeof(TraceComponents);
    var hasFlagsAttribute = type.GetCustomAttributes(typeof(FlagsAttribute), false).Length > 0;

    await Assert.That(hasFlagsAttribute).IsTrue();
  }

  // ==========================================================================
  // Convenience Combination Tests
  // ==========================================================================

  [Test]
  public async Task AllWithoutWorkers_ExcludesOnlyWorkersAsync() {
    var combo = TraceComponents.AllWithoutWorkers;

    // Should include everything except Workers
    await Assert.That(combo.HasFlag(TraceComponents.Handlers)).IsTrue();
    await Assert.That(combo.HasFlag(TraceComponents.Lifecycle)).IsTrue();
    await Assert.That(combo.HasFlag(TraceComponents.Dispatcher)).IsTrue();
    await Assert.That(combo.HasFlag(TraceComponents.Messages)).IsTrue();
    await Assert.That(combo.HasFlag(TraceComponents.Events)).IsTrue();
    await Assert.That(combo.HasFlag(TraceComponents.Outbox)).IsTrue();
    await Assert.That(combo.HasFlag(TraceComponents.Inbox)).IsTrue();
    await Assert.That(combo.HasFlag(TraceComponents.EventStore)).IsTrue();
    await Assert.That(combo.HasFlag(TraceComponents.Perspectives)).IsTrue();
    await Assert.That(combo.HasFlag(TraceComponents.Tags)).IsTrue();
    await Assert.That(combo.HasFlag(TraceComponents.Security)).IsTrue();
    await Assert.That(combo.HasFlag(TraceComponents.Errors)).IsTrue();

    // Should NOT include Workers
    await Assert.That(combo.HasFlag(TraceComponents.Workers)).IsFalse();
  }

  [Test]
  public async Task Core_IncludesHandlersLifecycleDispatcherMessagesAsync() {
    var combo = TraceComponents.Core;

    await Assert.That(combo.HasFlag(TraceComponents.Handlers)).IsTrue();
    await Assert.That(combo.HasFlag(TraceComponents.Lifecycle)).IsTrue();
    await Assert.That(combo.HasFlag(TraceComponents.Dispatcher)).IsTrue();
    await Assert.That(combo.HasFlag(TraceComponents.Messages)).IsTrue();

    // Should NOT include other components
    await Assert.That(combo.HasFlag(TraceComponents.Outbox)).IsFalse();
    await Assert.That(combo.HasFlag(TraceComponents.Workers)).IsFalse();
  }

  [Test]
  public async Task Messaging_IncludesMessagesEventsOutboxInboxAsync() {
    var combo = TraceComponents.Messaging;

    await Assert.That(combo.HasFlag(TraceComponents.Messages)).IsTrue();
    await Assert.That(combo.HasFlag(TraceComponents.Events)).IsTrue();
    await Assert.That(combo.HasFlag(TraceComponents.Outbox)).IsTrue();
    await Assert.That(combo.HasFlag(TraceComponents.Inbox)).IsTrue();

    // Should NOT include other components
    await Assert.That(combo.HasFlag(TraceComponents.Handlers)).IsFalse();
    await Assert.That(combo.HasFlag(TraceComponents.Workers)).IsFalse();
  }

  [Test]
  public async Task Storage_IncludesEventStorePerspectivesAsync() {
    var combo = TraceComponents.Storage;

    await Assert.That(combo.HasFlag(TraceComponents.EventStore)).IsTrue();
    await Assert.That(combo.HasFlag(TraceComponents.Perspectives)).IsTrue();

    // Should NOT include other components
    await Assert.That(combo.HasFlag(TraceComponents.Handlers)).IsFalse();
    await Assert.That(combo.HasFlag(TraceComponents.Workers)).IsFalse();
  }

  [Test]
  public async Task Production_IncludesHandlersErrorsSecurityAsync() {
    var combo = TraceComponents.Production;

    await Assert.That(combo.HasFlag(TraceComponents.Handlers)).IsTrue();
    await Assert.That(combo.HasFlag(TraceComponents.Errors)).IsTrue();
    await Assert.That(combo.HasFlag(TraceComponents.Security)).IsTrue();

    // Should NOT include noisy components
    await Assert.That(combo.HasFlag(TraceComponents.Workers)).IsFalse();
    await Assert.That(combo.HasFlag(TraceComponents.Lifecycle)).IsFalse();
  }
}
