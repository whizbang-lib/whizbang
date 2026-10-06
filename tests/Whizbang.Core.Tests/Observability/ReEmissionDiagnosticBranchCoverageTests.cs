// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Branch backfill for <see cref="ReEmissionDiagnostic"/>: a consumed-type re-emission on a host
/// with no <see cref="DispatcherMetrics"/> registered still warns.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/ReEmissionDiagnostic.cs</code-under-test>
[Category("Shard2")]
public sealed class ReEmissionDiagnosticBranchCoverageTests {

  private sealed record ConsumedEvent : IEvent;

  private sealed class StubRegistryQuery : IReceptorRegistryQuery {
    public bool HasAnyConsumer(string messageType) => false;
    public bool HasInboxHandler(string messageType) => false;
    public bool HasReceptors(LifecycleStage stage, string messageType) => false;
    public IReadOnlyList<HandledMessageInfo> GetHandledMessages() =>
      [new HandledMessageInfo(TypeNameFormatter.Format(typeof(ConsumedEvent)), "tests", Whizbang.Core.Routing.MessageKind.Event)];
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

  [Test]
  public async Task Emit_OfConsumedType_WithoutMetrics_StillWarnsOnceAsync() {
    var log = new CaptureLogger();
    var diag = new ReEmissionDiagnostic(new StubRegistryQuery(), log, metrics: null);
    var wire = TypeNameFormatter.Format(typeof(ConsumedEvent));

    diag.RecordEmission(wire);
    diag.RecordEmission(wire);

    List<(LogLevel Level, string Message)> entries;
    lock (log.Entries) { entries = [.. log.Entries]; }
    await Assert.That(entries.Count(e => e.Level == LogLevel.Warning)).IsEqualTo(1)
      .Because("with no metrics registered the counter is skipped, but the once-per-type warning still fires");
    await Assert.That(entries[0].Message).Contains("PUBLISHES");
  }
}
