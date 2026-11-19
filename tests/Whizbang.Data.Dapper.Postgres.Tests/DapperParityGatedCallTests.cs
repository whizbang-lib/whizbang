// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The coordinator operations the Dapper driver gained for parity with EF Core (#2051) take a slot of the coordinator
/// gate before they touch the pool, under their own member name, and give it back whatever they return.
/// </summary>
/// <remarks>
/// The gate records a slot's hold duration, tagged with the acquiring member's name, only when the slot is released, so a
/// measurement under the expected name proves the member took a slot and handed it back, and an empty holder list
/// afterwards proves nothing leaked. One case per shape of call: a single statement, a connection held for several,
/// the queue-index guard that forwards its caller, the multi-statement cleanup, the coalesce fold's transaction and the
/// deadline release inside its deadlock retry. The EF Core twin is <c>EFCoreWorkCoordinatorGatedCallTests</c>.
/// </remarks>
/// <docs>data/drivers#dapper-coordinator-parity</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.RetentionSweeps.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.TableMaintenance.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.Coalesce.cs</code-under-test>
[Category("Integration")]
public class DapperParityGatedCallTests : PostgresTestBase {
  private const string HOLD_INSTRUMENT = "whizbang.gate.hold_duration_ms";

  private static readonly Dictionary<string, Func<DapperWorkCoordinator, Task>> _calls = new(StringComparer.Ordinal) {
    [nameof(DapperWorkCoordinator.GetQueueIndexBloatAsync)] = c => c.GetQueueIndexBloatAsync(),
    [nameof(DapperWorkCoordinator.VacuumWorkTablesAsync)] = c => c.VacuumWorkTablesAsync(),
    [nameof(DapperWorkCoordinator.DropInvalidQueueIndexAsync)] = c => c.DropInvalidQueueIndexAsync("wh_active_streams_pkey"),
    [nameof(DapperWorkCoordinator.CleanupLifecycleCompletionsAsync)] = c => c.CleanupLifecycleCompletionsAsync(TimeSpan.FromDays(7)),
    [nameof(DapperWorkCoordinator.CompleteCoalesceFoldAsync)] = c => c.CompleteCoalesceFoldAsync([Guid.NewGuid()], [], partitionCount: 100),
    [nameof(DapperWorkCoordinator.ReleaseMaturedCoalesceAsync)] = c => c.ReleaseMaturedCoalesceAsync("group-gated"),
    [nameof(DapperWorkCoordinator.RequestStandbyAsync)] = c => c.RequestStandbyAsync(Guid.NewGuid(), "2.0.0"),
  };

  /// <summary>The case keys, one test per gated call.</summary>
  public static IEnumerable<string> GatedCalls() => _calls.Keys.Order(StringComparer.Ordinal);

  [Test]
  [MethodDataSource(nameof(GatedCalls))]
  public async Task GatedCall_TakesAGateSlotUnderItsOwnName_AndReleasesItAsync(string call) {
    using var probe = new GateProbe();
    var coordinator = new DapperWorkCoordinator(
      ConnectionString, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(), gate: probe.Gate);

    await _calls[call](coordinator);

    await Assert.That(probe.Callers).Contains(call)
      .Because("a hold is recorded only on release, so a measurement under this name proves the member took a slot and gave it back");
    await Assert.That(probe.Gate.SnapshotHolders()).IsEmpty()
      .Because("a slot still held after the call returns starves every later caller");
  }

  private sealed class GateProbe : IDisposable {
    private readonly ServiceProvider _services;
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<string> _callers = new();

    public GateProbe() {
      _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
      var metrics = new WorkCoordinatorMetrics(new WhizbangMetrics(_services.GetRequiredService<IMeterFactory>()));
      Gate = new WorkCoordinatorGate(4, NullLogger<WorkCoordinatorGate>.Instance, metrics: metrics);
      var meter = metrics.GateHoldDuration.Meter;
      _listener.InstrumentPublished = (instrument, listener) => {
        if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == HOLD_INSTRUMENT) {
          listener.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<double>((_, _, tags, _) => {
        foreach (var tag in tags) {
          if (tag.Key == "caller" && tag.Value is string caller) {
            _callers.Enqueue(caller);
          }
        }
      });
      _listener.Start();
    }

    public WorkCoordinatorGate Gate { get; }

    public IReadOnlyList<string> Callers => [.. _callers];

    public void Dispose() {
      _listener.Dispose();
      Gate.Dispose();
      _services.Dispose();
    }
  }
}
