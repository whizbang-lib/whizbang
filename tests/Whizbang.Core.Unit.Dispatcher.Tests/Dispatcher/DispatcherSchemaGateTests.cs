// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// The write seam (#1877): a dispatch needs the event store and the outbox, which Migrate provides, so
/// <see cref="Core.Dispatcher"/> holds a dispatch made while the schema gate is closed until the migration
/// completes, then lets it run. The wait honors the caller's cancellation and is bounded by
/// <c>SchemaInitializationOptions.DispatchMaxWait</c> (five minutes unless configured), after which the dispatch
/// fails with <see cref="WhizbangNotReadyException"/>. A fixture without a gate stays ungated.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Startup")]
public class DispatcherSchemaGateTests {

  private const string MAX_WAIT_KEY = "Whizbang:SchemaInitialization:DispatchMaxWait";

  private sealed record PlaceOrder(Guid OrderId);

  private sealed class SeamDispatcher(IServiceProvider sp) : Core.Dispatcher(
      sp, new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build())) {
    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType)
      => _ => ValueTask.FromResult<TResult>(default!);
    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType)
      => null;
    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType)
      => _ => Task.CompletedTask;
    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType)
      => null;
    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType)
      => null;
    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType)
      => null;
    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType)
      => _ => ValueTask.FromResult<object?>(null);
    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType)
      => DispatchModes.LocalDispatch;
  }

  [Test]
  public async Task Send_WithTheGateClosed_DefersUntilTheMigrationCompletesAsync() {
    var gate = new SchemaReadyGate();   // NOT ready: migrations in flight
    await using var sp = _provider(gate, new FakeTimeProvider());
    var dispatcher = new SeamDispatcher(sp);

    var send = dispatcher.SendAsync(new PlaceOrder(Guid.NewGuid()));
    await Assert.That(send.IsCompleted).IsFalse()
      .Because("a dispatch made during the migration waits for it instead of being refused");

    gate.MarkReady();

    await Assert.That(await send).IsNotNull()
      .Because("the deferred dispatch runs once the migration completes");
  }

  [Test]
  public async Task Publish_WithTheGateClosed_DefersUntilTheMigrationCompletesAsync() {
    var gate = new SchemaReadyGate();
    await using var sp = _provider(gate, new FakeTimeProvider());
    var dispatcher = new SeamDispatcher(sp);

    var publish = dispatcher.PublishAsync(new PlaceOrder(Guid.NewGuid()));
    await Assert.That(publish.IsCompleted).IsFalse();

    gate.MarkReady();

    await Assert.That(await publish).IsNotNull();
  }

  [Test]
  public async Task DeferredDispatch_ByDefault_WaitsAtMostFiveMinutesThenThrowsNotReadyAsync() {
    var time = new FakeTimeProvider();
    await using var sp = _provider(new SchemaReadyGate(), time);
    var dispatcher = new SeamDispatcher(sp);

    var send = dispatcher.SendAsync(new PlaceOrder(Guid.NewGuid()));
    time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
    await Assert.That(send.IsCompleted).IsFalse()
      .Because("the default maximum wait is five minutes, and it has not elapsed");

    time.Advance(TimeSpan.FromTicks(1));

    await Assert.ThrowsAsync<WhizbangNotReadyException>(async () => await send);
  }

  [Test]
  public async Task DeferredDispatch_WithAConfiguredMaxWait_ThrowsNotReadyWhenThatWaitElapsesAsync() {
    var time = new FakeTimeProvider();
    await using var sp = _provider(new SchemaReadyGate(), time, maxWait: "00:00:30");
    var dispatcher = new SeamDispatcher(sp);

    var send = dispatcher.SendAsync(new PlaceOrder(Guid.NewGuid()));
    time.Advance(TimeSpan.FromSeconds(29));
    await Assert.That(send.IsCompleted).IsFalse();

    time.Advance(TimeSpan.FromSeconds(1));

    await Assert.ThrowsAsync<WhizbangNotReadyException>(async () => await send);
  }

  [Test]
  public async Task DeferredDispatch_WithAZeroMaxWait_RefusesAtOnceAsync() {
    await using var sp = _provider(new SchemaReadyGate(), new FakeTimeProvider(), maxWait: "00:00:00");
    var dispatcher = new SeamDispatcher(sp);

    await Assert.ThrowsAsync<WhizbangNotReadyException>(async () =>
      await dispatcher.SendAsync(new PlaceOrder(Guid.NewGuid())));
  }

  [Test]
  public async Task DeferredDispatch_WhenTheCallerCancels_StopsWaitingAsync() {
    await using var sp = _provider(new SchemaReadyGate(), new FakeTimeProvider());
    var dispatcher = new SeamDispatcher(sp);
    using var cts = new CancellationTokenSource();

    var send = dispatcher.SendAsync(new PlaceOrder(Guid.NewGuid()), new DispatchOptions().WithCancellationToken(cts.Token));
    await Assert.That(send.IsCompleted).IsFalse();

    await cts.CancelAsync();

    await Assert.ThrowsAsync<OperationCanceledException>(async () => await send);
  }

  [Test]
  public async Task DeferredDispatch_LogsOnceThatDispatchIsDeferredUntilTheMigrationCompletesAsync() {
    var gate = new SchemaReadyGate();
    var services = _services(gate, new FakeTimeProvider());
    services.AddLogging(b => {
      b.SetMinimumLevel(LogLevel.Trace);
      b.AddFakeLogging();
    });
    await using var sp = services.BuildServiceProvider();
    var dispatcher = new SeamDispatcher(sp);

    var first = dispatcher.SendAsync(new PlaceOrder(Guid.NewGuid()));
    var second = dispatcher.PublishAsync(new PlaceOrder(Guid.NewGuid()));
    gate.MarkReady();
    await first;
    await second;

    var deferred = sp.GetFakeLogCollector().GetSnapshot()
      .Where(r => r.Message.Contains("deferred until the migration completes", StringComparison.Ordinal))
      .ToList();
    await Assert.That(deferred.Count).IsEqualTo(1)
      .Because("the deferral is announced once, not once per waiting dispatch");
  }

  [Test]
  public async Task Send_OnceTheGateOpens_ProceedsAsync() {
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var services = new ServiceCollection();
    services.AddSingleton<ISchemaReadyGate>(gate);
    await using var sp = services.BuildServiceProvider();
    var dispatcher = new SeamDispatcher(sp);

    var receipt = await dispatcher.SendAsync(new PlaceOrder(Guid.NewGuid()));

    await Assert.That(receipt).IsNotNull()
      .Because("the seam holds during Migrate and only during Migrate; writes resume when it completes");
  }

  [Test]
  public async Task Send_WithNoGateRegistered_StaysUngatedAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var dispatcher = new SeamDispatcher(sp);

    var receipt = await dispatcher.SendAsync(new PlaceOrder(Guid.NewGuid()));

    await Assert.That(receipt).IsNotNull()
      .Because("fixtures and hosts without the worker pipeline behave exactly as before");
  }

  [Test]
  public async Task HoldingTheApplicationUntilMigrated_StaysOptInAsync() {
    // The hold option is NonBlockingSchemaInit = false: the host does not finish starting, so nothing
    // dispatches, until the migration completes. The owner kept it opt-in (#1877); deferral is the default.
    await Assert.That(new SchemaInitializationOptions().NonBlockingSchemaInit).IsTrue();
  }

  private static ServiceProvider _provider(ISchemaReadyGate gate, FakeTimeProvider time, string? maxWait = null)
    => _services(gate, time, maxWait).BuildServiceProvider();

  private static ServiceCollection _services(ISchemaReadyGate gate, FakeTimeProvider time, string? maxWait = null) {
    var services = new ServiceCollection();
    services.AddSingleton(gate);
    services.AddSingleton<TimeProvider>(time);
    if (maxWait is not null) {
      var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [MAX_WAIT_KEY] = maxWait })
        .Build();
      services.AddOptions<SchemaInitializationOptions>()
        .Bind(configuration.GetSection("Whizbang:SchemaInitialization"));
    }
    return services;
  }
}
