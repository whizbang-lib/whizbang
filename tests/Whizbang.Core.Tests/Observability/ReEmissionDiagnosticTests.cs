using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// <para>Locks the re-emission cascade diagnostic (issue #587): a service that PUBLISHES an
/// event type it also CONSUMES is the amplification shape — each hop can re-raise what it
/// just handled, and nothing surfaced it (a prior static-registry attempt shipped with no
/// caller and was deleted). This diagnostic is RUNTIME observation: at publish, the emitted
/// type is checked against the receptor registry's consumed set — a match counts on
/// <c>whizbang.dispatcher.re_emissions</c> (tagged by type) and warns once per type.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/ReEmissionDiagnostic.cs</code-under-test>
/// <docs>fundamentals/messaging/publishing-events</docs>
[Category("Shard2")]
public sealed class ReEmissionDiagnosticTests {

  private sealed record ConsumedEvent : IEvent;

  private sealed class StubRegistryQuery : IReceptorRegistryQuery {
    public bool HasAnyConsumer(string messageTypeName) => false;
    public bool HasInboxHandler(string messageTypeName) => false;
    public bool HasReceptors(LifecycleStage stage, string messageTypeName) => false;
    public IReadOnlyList<HandledMessageInfo> GetHandledMessages() =>
      [new HandledMessageInfo(Whizbang.Core.TypeNameFormatter.Format(typeof(ConsumedEvent)), "tests", Whizbang.Core.Routing.MessageKind.Event)];
  }

  private sealed class CaptureLogger : ILogger<ReEmissionDiagnostic> {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (Entries) { Entries.Add((logLevel, formatter(state, exception))); }
    }
  }

  private static (ReEmissionDiagnostic Diag, CaptureLogger Log, Func<long> Count) _arm() {
    var metrics = new DispatcherMetrics(new WhizbangMetrics());
    long count = 0;
    var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument, metrics.ReEmissions)) { l.EnableMeasurementEvents(instrument); }
    };
    listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref count, value));
    listener.Start();
    var log = new CaptureLogger();
    var diag = new ReEmissionDiagnostic(new StubRegistryQuery(), log, metrics);
    return (diag, log, () => { listener.RecordObservableInstruments(); return Interlocked.Read(ref count); });
  }

  [Test]
  public async Task Emit_OfConsumedType_CountsAndWarnsOnceAsync() {
    var (diag, log, count) = _arm();
    var wire = Whizbang.Core.TypeNameFormatter.Format(typeof(ConsumedEvent));

    diag.RecordEmission(wire);
    diag.RecordEmission(wire);

    await Assert.That(count()).IsEqualTo(2L)
      .Because("every re-emission counts — the METER is the trend an operator alarms on, "
             + "and a 3.2x amplification factor is invisible without it");
    List<(LogLevel, string)> entries;
    lock (log.Entries) { entries = [.. log.Entries]; }
    await Assert.That(entries.Count(e => e.Item1 == LogLevel.Warning)).IsEqualTo(1)
      .Because("the LOG warns once per type — a cascade under load must not become its own "
             + "log storm");
  }

  [Test]
  public async Task Emit_OfUnconsumedType_IsSilentAsync() {
    var (diag, log, count) = _arm();

    diag.RecordEmission("Some.Other.Type, Other");

    await Assert.That(count()).IsEqualTo(0L)
      .Because("emitting a type this service does not consume is the NORMAL shape — only "
             + "the consume-and-re-raise intersection is the cascade signature");
    await Assert.That(log.Entries.Count).IsEqualTo(0);
  }

  [Test]
  public async Task NullRegistry_IsInertAsync() {
    var metrics = new DispatcherMetrics(new WhizbangMetrics());
    var diag = new ReEmissionDiagnostic(null, null, metrics);
    var act = () => diag.RecordEmission("Any.Type, Asm");
    await Assert.That(act).ThrowsNothing()
      .Because("hosts without the registry stay safe — the diagnostic degrades to inert");
  }

  private sealed class PublishProbeDispatcher(IServiceProvider sp)
      : Whizbang.Core.Dispatcher(sp, new ServiceInstanceProvider(configuration: null)) {
    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) => null;
    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) => _ => Task.CompletedTask;
    protected override Func<object, Whizbang.Core.Observability.IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) => (_, _, _) => Task.CompletedTask;
    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) => null;
    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) => null;
    protected override Whizbang.Core.Dispatch.DispatchModes? GetReceptorDefaultRouting(Type messageType) => null;
  }

  [Whizbang.Core.Dispatch.DefaultRouting(Whizbang.Core.Dispatch.DispatchModes.Local)]
  private sealed record ProbeEvent([property: Whizbang.Core.StreamId] Guid Id) : IEvent;

  [Test]
  public async Task Dispatcher_Publish_OfConsumedType_FiresTheDiagnosticAsync() {
    // The wiring lock: the diagnostic only matters if the PUBLISH seam actually calls it —
    // a prior attempt at #587 shipped a reporter with zero callers and was deleted.
    var metrics = new DispatcherMetrics(new WhizbangMetrics());
    long count = 0;
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument, metrics.ReEmissions)) { l.EnableMeasurementEvents(instrument); }
    };
    listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref count, value));
    listener.Start();

    var registry = new ConsumesProbeRegistry();
    var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
    Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<IReceptorRegistryQuery>(services, registry);
    Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(services, new ReEmissionDiagnostic(registry, null, metrics));
    var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
    var dispatcher = new PublishProbeDispatcher(sp);

    await dispatcher.PublishAsync(new ProbeEvent(Guid.NewGuid()));

    await Assert.That(Interlocked.Read(ref count)).IsEqualTo(1L)
      .Because("PublishAsync is the emission seam — the diagnostic fires there or it is scenery");
  }

  private sealed class ConsumesProbeRegistry : IReceptorRegistryQuery {
    public bool HasAnyConsumer(string messageTypeName) => false;
    public bool HasInboxHandler(string messageTypeName) => false;
    public bool HasReceptors(LifecycleStage stage, string messageTypeName) => false;
    public IReadOnlyList<HandledMessageInfo> GetHandledMessages() =>
      [new HandledMessageInfo(Whizbang.Core.TypeNameFormatter.Format(typeof(ProbeEvent)), "tests", Whizbang.Core.Routing.MessageKind.Event)];
  }

}
