using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Report-only is bilateral on the consumer side: a <see cref="RedeliveryComposite"/> reaching a consumer
/// whose <see cref="StreamIntegrityOptions.RepairMode"/> is <see cref="IntegrityRepairMode.ReportOnly"/>
/// completes without fan-out (the bundle row is deleted, no children, no pre-fanout receptors), an opted-in
/// consumer fans it out, and ordinary composites are untouched by the mode.
/// </summary>
public partial class InboxDispatchWorkerTests {
  private sealed record _dispatchLogEntry(LogLevel Level, string Message);

  private sealed class _capturingDispatchLogger : ILogger<InboxDispatchWorker> {
    private readonly List<_dispatchLogEntry> _entries = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => _nullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_entries) { _entries.Add(new _dispatchLogEntry(logLevel, formatter(state, exception))); }
    }
    public List<_dispatchLogEntry> Snapshot() { lock (_entries) { return [.. _entries]; } }
    private sealed class _nullScope : IDisposable {
      public static readonly _nullScope Instance = new();
      public void Dispose() { }
    }
  }

  private static RedeliveryComposite _redeliveryBundle(int innerCount) {
    var composite = new RedeliveryComposite { OriginServiceId = (Guid)TrackedGuid.NewMedo() };
    for (var i = 0; i < innerCount; i++) {
      composite.InnerPayloads.Add(JsonDocument.Parse("{}").RootElement);
      composite.InnerTypeNames.Add(typeof(_innerImportEvent).AssemblyQualifiedName!);
      composite.InnerEventIds.Add((Guid)TrackedGuid.NewMedo());
    }
    return composite;
  }

  private static async Task<(HandlerCommitRequest Routed, List<_dispatchLogEntry> Log)> _runCompositeUnderRepairModeAsync(
      ICompositeEvent composite, IntegrityRepairMode repairMode) {
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var sp = new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new FakeEnvelopeSerializer())
      .AddSingleton<IReceptorInvoker>(new DirectiveInvoker(FanoutDirective.Proceed))
      .BuildServiceProvider();
    var logger = new _capturingDispatchLogger();
    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeInstanceProvider(), inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions { PartitionCount = 1 }),
      Options.Create(new WorkCoordinatorOptions()),
      logger,
      integrityOptions: Options.Create(new StreamIntegrityOptions { RepairMode = repairMode }),
      lifecycleMessageDeserializer: new FakeCompositeDeserializer(composite),
      receptorRegistry: new PostInboxInlineRegistry());
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await inbox.WriteAsync(_makeWork(), cts.Token);
    var routed = await handlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
    return (routed, logger.Snapshot());
  }

  [Test]
  public async Task RedeliveryBundle_UnderReportOnly_CompletesWithoutFanOutAsync() {
    var (routed, log) = await _runCompositeUnderRepairModeAsync(_redeliveryBundle(2), IntegrityRepairMode.ReportOnly);

    await Assert.That(routed.NewInboxMessages is null || routed.NewInboxMessages.Count == 0).IsTrue()
      .Because("a consumer that opted down from repair folds in no repair: the bundle produces no children");
    await Assert.That(routed.InboxCompletion.Status).IsEqualTo((int)MessageProcessingStatus.EventStored)
      .Because("the bundle row is deleted like a skipped composite: never event-stored, never retried");
    await Assert.That(log.Any(e => e.Message.Contains("RepairMode is ReportOnly", StringComparison.Ordinal))).IsTrue()
      .Because("an operator must be able to see that repair data was dropped and why");
  }

  [Test]
  public async Task RedeliveryBundle_UnderAutoRepairCapped_FansOutAsync() {
    var (routed, log) = await _runCompositeUnderRepairModeAsync(_redeliveryBundle(2), IntegrityRepairMode.AutoRepairCapped);

    await Assert.That(routed.NewInboxMessages?.Count ?? 0).IsEqualTo(2)
      .Because("the opted-in consumer folds the bundle in: one child row per inner event");
    await Assert.That(log.Any(e => e.Message.Contains("RepairMode is ReportOnly", StringComparison.Ordinal))).IsFalse();
  }

  [Test]
  public async Task OrdinaryComposite_UnderReportOnly_StillFansOutAsync() {
    var composite = new _bulkComposite(new _innerImportEvent("J-1"), new _innerImportEvent("J-2"));

    var (routed, _) = await _runCompositeUnderRepairModeAsync(composite, IntegrityRepairMode.ReportOnly);

    await Assert.That(routed.NewInboxMessages?.Count ?? 0).IsEqualTo(2)
      .Because("the mode governs repair traffic only; a domain composite is not repair");
  }
}
