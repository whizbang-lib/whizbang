// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Branches of <see cref="PerspectiveMigrationWorker"/> the sibling suites leave untaken: a host stop
/// that lands between two pending rebuilds, and a failed rebuild that reports no error text.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveMigrationWorker.cs</code-under-test>
[Category("Workers")]
public class PerspectiveMigrationWorkerBranchCoverageTests {

  private static readonly TimeSpan _wait = TimeSpan.FromSeconds(20);

  private sealed class ScriptedRebuilder(Func<string, RebuildResult> respond) : IPerspectiveRebuilder {
    public List<string> Rebuilt { get; } = [];
    public Action? AfterRebuild { get; set; }

    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, CancellationToken ct = default) {
      Rebuilt.Add(perspectiveName);
      var result = respond(perspectiveName);
      AfterRebuild?.Invoke();
      return Task.FromResult(result);
    }
    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct = default) =>
      throw new NotSupportedException();
    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct = default) =>
      throw new NotSupportedException();
    public Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<RebuildStatus?>(null);
  }

  /// <summary>Yields before answering, as a rebuild that runs against the database does.</summary>
  private sealed class YieldingRebuilder(Func<string, RebuildResult> respond) : IPerspectiveRebuilder {
    public async Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, CancellationToken ct = default) {
      await Task.Yield();
      return respond(perspectiveName);
    }
    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct = default) =>
      throw new NotSupportedException();
    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct = default) =>
      throw new NotSupportedException();
    public Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<RebuildStatus?>(null);
  }

  private sealed class CapturingLogger : ILogger<PerspectiveMigrationWorker> {
    private readonly List<string> _messages = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      ArgumentNullException.ThrowIfNull(formatter);
      lock (_messages) { _messages.Add(formatter(state, exception)); }
    }
    public List<string> Snapshot() { lock (_messages) { return [.. _messages]; } }
  }

  [Test]
  public async Task ExecuteAsync_HostStopsAfterTheFirstRebuild_LeavesTheRemainingRebuildsPendingAsync() {
    // A stop between rebuilds must leave the rest for the next start: their rows stay pending and
    // the next host picks them up, instead of this host starting a long rebuild it will not finish.
    using var stopping = new CancellationTokenSource();
    var rebuilder = new ScriptedRebuilder(name => new RebuildResult(name, 1, 1, TimeSpan.Zero, true, null)) {
      AfterRebuild = stopping.Cancel,
    };
    var statusUpdates = new List<string>();
    var worker = new PerspectiveMigrationWorker(
      rebuilder: rebuilder,
      logger: new CapturingLogger(),
      schemaReadyGate: SchemaReadyGate.AlreadyReady()) {
      GetPendingRebuilds = _ => Task.FromResult<IReadOnlyList<PendingMigrationRebuild>>([
        new PendingMigrationRebuild("FirstPerspective", "perspective:FirstPerspective"),
        new PendingMigrationRebuild("SecondPerspective", "perspective:SecondPerspective"),
      ]),
      UpdateMigrationStatus = (key, _, _, _) => {
        lock (statusUpdates) { statusUpdates.Add(key); }
        return Task.CompletedTask;
      },
    };

    await worker.StartAsync(stopping.Token);
    // The stop is requested from inside the first rebuild, so the body has provably run by then;
    // its own completion is the signal that the loop reached its decision about the second item.
    await worker.ExecuteTask!.WaitAsync(_wait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(rebuilder.Rebuilt).IsEquivalentTo(["FirstPerspective"])
      .Because("the second rebuild must not start once the host has asked to stop");
    await Assert.That(statusUpdates).IsEquivalentTo(["perspective:FirstPerspective"])
      .Because("the rebuild already finished still records its outcome; the skipped one stays pending");
    await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse();
  }

  [Test]
  public async Task ExecuteAsync_RebuildFailsWithoutAnErrorMessage_LogsUnknownAsTheReasonAsync() {
    var rebuilder = new ScriptedRebuilder(name => new RebuildResult(name, 0, 0, TimeSpan.Zero, false, null));
    var logger = new CapturingLogger();
    var statuses = new List<(int Status, string Description)>();
    var worker = new PerspectiveMigrationWorker(
      rebuilder: rebuilder,
      logger: logger,
      schemaReadyGate: SchemaReadyGate.AlreadyReady()) {
      GetPendingRebuilds = _ => Task.FromResult<IReadOnlyList<PendingMigrationRebuild>>([
        new PendingMigrationRebuild("SilentPerspective", "perspective:SilentPerspective"),
      ]),
      UpdateMigrationStatus = (_, status, description, _) => {
        lock (statuses) { statuses.Add((status, description)); }
        return Task.CompletedTask;
      },
    };

    await worker.StartAsync(CancellationToken.None);
    await worker.ExecuteTask!.WaitAsync(_wait);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(statuses).Count().IsEqualTo(1);
    await Assert.That(statuses[0].Status).IsEqualTo(-1).Because("a failed rebuild is recorded as failed");
    var failureLine = logger.Snapshot().SingleOrDefault(m => m.Contains("Migration rebuild failed for SilentPerspective", StringComparison.Ordinal));
    await Assert.That(failureLine).IsNotNull();
    await Assert.That(failureLine).EndsWith(": unknown")
      .Because("a failure with no error text still names a reason, rather than an empty one an operator cannot search for");
  }

  [Test]
  public async Task ExecuteAsync_RebuildAndStatusWritesCompleteAsynchronously_RecordsEachOutcomeInOrderAsync() {
    // The sibling tests answer every await synchronously, so the loop never resumes inside its
    // per-rebuild try block. Real rebuilds and status writes suspend on the database: after resuming
    // from the rebuild, from a success write and from a failure write, each outcome must still be
    // recorded against its own migration, and the next rebuild must still run.
    var rebuilder = new YieldingRebuilder(name => name == "BrokenPerspective"
      ? new RebuildResult(name, 0, 0, TimeSpan.Zero, false, "projection threw")
      : new RebuildResult(name, 4, 9, TimeSpan.FromSeconds(1), true, null));
    var statuses = new List<(string Key, int Status, string Description)>();
    var worker = new PerspectiveMigrationWorker(
      rebuilder: rebuilder,
      logger: new CapturingLogger(),
      schemaReadyGate: SchemaReadyGate.AlreadyReady()) {
      GetPendingRebuilds = _ => Task.FromResult<IReadOnlyList<PendingMigrationRebuild>>([
        new PendingMigrationRebuild("HealthyPerspective", "perspective:HealthyPerspective"),
        new PendingMigrationRebuild("BrokenPerspective", "perspective:BrokenPerspective"),
        new PendingMigrationRebuild("LaterPerspective", "perspective:LaterPerspective"),
      ]),
      UpdateMigrationStatus = async (key, status, description, _) => {
        await Task.Yield();
        lock (statuses) { statuses.Add((key, status, description)); }
      },
    };

    await worker.StartAsync(CancellationToken.None);
    await worker.ExecuteTask!.WaitAsync(_wait);
    await worker.StopAsync(CancellationToken.None);

    List<(string Key, int Status, string Description)> recorded;
    lock (statuses) { recorded = [.. statuses]; }
    await Assert.That(recorded.ConvertAll(s => (s.Key, s.Status))).IsEquivalentTo(
        [("perspective:HealthyPerspective", 2), ("perspective:BrokenPerspective", -1), ("perspective:LaterPerspective", 2)],
        TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("each rebuild's outcome is written to its own migration row, and a failure resumed from an "
             + "asynchronous write does not stop the rebuilds after it");
    await Assert.That(recorded[1].Description).IsEqualTo("Failed: projection threw")
      .Because("the failure text the rebuild reported is what the operator sees on the migration row");
  }
}
