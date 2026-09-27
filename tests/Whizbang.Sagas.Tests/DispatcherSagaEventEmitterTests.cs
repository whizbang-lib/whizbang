using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.ValueObjects;
using Whizbang.Sagas.Services;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Locks the bridge between <see cref="ISagaEventEmitter"/> and
/// <see cref="IDispatcher"/> — specifically that
/// <c>PublishAsync(TEvent, DateTimeOffset?)</c> with a non-null scheduledFor
/// routes through the dispatcher's options overload with
/// <see cref="DispatchOptions.ScheduledFor"/> populated, and that null
/// scheduledFor falls back to the simple PublishAsync path so the immediate-
/// dispatch contract is preserved verbatim.
/// </summary>
[Category("Unit")]
[Category("Saga")]
public class DispatcherSagaEventEmitterTests {

  /// <summary>A test-only event to drive the dispatcher's generic surface.</summary>
  public sealed class TestEvent : IEvent {
    [StreamId] public Guid EntityId { get; set; }
    public Guid MessageId { get; set; } = TrackedGuid.New().Value;
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CorrelationId { get; set; }
    public Guid? CausationId { get; set; }
  }

  [Test]
  public async Task PublishAsync_WithScheduledFor_RoutesThroughOptionsOverloadAsync() {
    var fireAt = DateTimeOffset.UtcNow.AddMinutes(7);
    var dispatcher = new RecordingDispatcher();
    var emitter = new DispatcherSagaEventEmitter(dispatcher);

    await emitter.PublishAsync(new TestEvent(), fireAt);

    await Assert.That(dispatcher.OptionsCallCount).IsEqualTo(1)
      .Because("Non-null scheduledFor must route through PublishAsync(event, DispatchOptions).");
    await Assert.That(dispatcher.SimpleCallCount).IsEqualTo(0)
      .Because("The simple PublishAsync overload must NOT be called when scheduledFor is non-null.");
    await Assert.That(dispatcher.LastCapturedOptions).IsNotNull();
    await Assert.That(dispatcher.LastCapturedOptions!.ScheduledFor)
      .IsEqualTo((DateTimeOffset?)fireAt)
      .Because("DispatchOptions.ScheduledFor must equal the value supplied to the emitter.");
  }

  [Test]
  public async Task PublishAsync_WithNullScheduledFor_FallsBackToImmediatePathAsync() {
    var dispatcher = new RecordingDispatcher();
    var emitter = new DispatcherSagaEventEmitter(dispatcher);

    await emitter.PublishAsync(new TestEvent(), scheduledFor: null);

    await Assert.That(dispatcher.SimpleCallCount).IsEqualTo(1)
      .Because("Null scheduledFor means immediate dispatch — go through the simple PublishAsync overload.");
    await Assert.That(dispatcher.OptionsCallCount).IsEqualTo(0)
      .Because("Don't pay for the options object allocation when scheduledFor is null.");
  }

  [Test]
  public async Task PublishAsync_NoSchedulingOverload_DelegatesToDispatcherAsync() {
    var dispatcher = new RecordingDispatcher();
    var emitter = new DispatcherSagaEventEmitter(dispatcher);

    await emitter.PublishAsync(new TestEvent());

    await Assert.That(dispatcher.SimpleCallCount).IsEqualTo(1);
    await Assert.That(dispatcher.OptionsCallCount).IsEqualTo(0);
  }

  /// <summary>
  /// The stranded-saga sweep publishes from a maintenance worker with no request of its own; the tick
  /// must reach the dispatcher as the system acting in the saga's tenant, claim-keyed.
  /// </summary>
  [Test]
  [NotInParallel("ScopeContextAccessor")]
  public async Task PublishOnceInTenantAsync_PublishesAsTheSystemInThatTenantAsync() {
    var dispatcher = new RecordingDispatcher();
    var emitter = new DispatcherSagaEventEmitter(dispatcher);

    var won = await ((ISagaEventEmitter)emitter).PublishOnceInTenantAsync("tenant-a", "sweep:1", new TestEvent(), CancellationToken.None);

    await Assert.That(won).IsTrue();
    await Assert.That(dispatcher.LastPublishOnceClaimKey).IsEqualTo("sweep:1");
    await Assert.That(dispatcher.LastPublishOnceScope?.TenantId).IsEqualTo("tenant-a")
      .Because("the tick is handled where it is received, in the scope it was published with");
    await Assert.That(dispatcher.LastPublishOnceScope?.UserId).IsEqualTo("SYSTEM");
  }

  /// <summary>Sagas that are not tenant-scoped are swept for all tenants.</summary>
  [Test]
  [NotInParallel("ScopeContextAccessor")]
  public async Task PublishOnceInTenantAsync_NoTenant_PublishesForAllTenantsAsync() {
    var dispatcher = new RecordingDispatcher();
    var emitter = new DispatcherSagaEventEmitter(dispatcher);

    await ((ISagaEventEmitter)emitter).PublishOnceInTenantAsync(null, "sweep:2", new TestEvent(), CancellationToken.None);

    await Assert.That(dispatcher.LastPublishOnceScope?.TenantId).IsEqualTo(Whizbang.Core.Lenses.TenantConstants.AllTenants);
  }

  /// <summary>
  /// The sweep's reads come before its publish and go through tenant-scoped lenses; the work must run
  /// as the system in the saga's tenant, and the worker's own context must come back afterwards.
  /// </summary>
  [Test]
  [NotInParallel("ScopeContextAccessor")]
  public async Task RunInTenantAsync_RunsTheWorkAsTheSystemInThatTenantAsync() {
    Whizbang.Core.Security.ScopeContextAccessor.CurrentContext = null;
    Whizbang.Core.Security.ScopeContextAccessor.CurrentInitiatingContext = null;
    var emitter = new DispatcherSagaEventEmitter(new RecordingDispatcher());
    var accessor = new Whizbang.Core.Security.ScopeContextAccessor();

    var seen = await ((ISagaEventEmitter)emitter).RunInTenantAsync(
      "tenant-a", _ => Task.FromResult(accessor.Current?.Scope), CancellationToken.None);

    await Assert.That(seen?.TenantId).IsEqualTo("tenant-a")
      .Because("a tenant-scoped lens throws when no ambient tenant is present");
    await Assert.That(seen?.UserId).IsEqualTo("SYSTEM");
    await Assert.That(Whizbang.Core.Security.ScopeContextAccessor.CurrentContext).IsNull()
      .Because("the next saga in the sweep may belong to another tenant");
  }

  /// <summary>A saga with no tenant is read exactly as before: in whatever context the worker has.</summary>
  [Test]
  [NotInParallel("ScopeContextAccessor")]
  public async Task RunInTenantAsync_NoTenant_RunsTheWorkInTheCallersContextAsync() {
    Whizbang.Core.Security.ScopeContextAccessor.CurrentContext = null;
    Whizbang.Core.Security.ScopeContextAccessor.CurrentInitiatingContext = null;
    var emitter = new DispatcherSagaEventEmitter(new RecordingDispatcher());
    using var cts = new CancellationTokenSource();

    var (scope, token) = await ((ISagaEventEmitter)emitter).RunInTenantAsync(
      null, ct => Task.FromResult((Whizbang.Core.Security.ScopeContextAccessor.CurrentContext, ct)), cts.Token);

    await Assert.That(scope).IsNull();
    await Assert.That(token).IsEqualTo(cts.Token);
  }

  [Test]
  public async Task RunInTenantAsync_NullWork_ThrowsAsync() {
    var emitter = new DispatcherSagaEventEmitter(new RecordingDispatcher());

    await Assert.That(async () => await ((ISagaEventEmitter)emitter).RunInTenantAsync<int>("tenant-a", null!, CancellationToken.None))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task PublishOnceAsync_ForwardsClaimKeyAndEventToDispatcherAsync() {
    var dispatcher = new RecordingDispatcher();
    var emitter = new DispatcherSagaEventEmitter(dispatcher);
    const string key = "saga-completed:bulk-job:abc";
    var evt = new TestEvent();

    var result = await emitter.PublishOnceAsync(key, evt, CancellationToken.None);

    await Assert.That(dispatcher.PublishOnceCallCount).IsEqualTo(1)
      .Because("PublishOnceAsync must route through IDispatcher.PublishOnceAsync exactly.");
    await Assert.That(dispatcher.LastPublishOnceClaimKey).IsEqualTo(key);
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task Constructor_NullDispatcher_ThrowsAsync() {
    DispatcherSagaEventEmitter? _ = null;
    await Assert.That(() => _ = new DispatcherSagaEventEmitter(null!)).ThrowsExactly<ArgumentNullException>();
  }
}
