// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The fan-out measurement of set-based writes (#2246, step 8): N new streams with one event each, applied through the
/// real pipeline in each write mode, counting the statements the batch issues against the perspective table and the
/// database as a whole, and the rows applied per second. A measurement, not a gate: it runs only when asked for by name.
/// </summary>
/// <remarks>
/// The stream count is <c>WHIZBANG_FANOUT_STREAMS</c> (100,000 by default). Completions take the path a service runs:
/// the default (batched) completion strategy and the completion flush worker.
/// The results, one <c>MEASURE</c> line per mode and the most frequent statement shapes, go to standard output and are
/// appended to the file <c>WHIZBANG_MEASURE_REPORT</c> names (by default <c>fan-out-measurement.txt</c> beside the test
/// assembly), since a passing test's output is captured.
/// </remarks>
public partial class SetBasedPerspectiveWorkerIntegrationTests {
  [Test]
  [Explicit]
  // One mode at a time, so neither measures the other's load; 100,000 streams take tens of minutes per mode.
  [NotInParallel("fan-out-measurement")] // the database: a mode measured beside the other measures its load too
  [Timeout(4 * 60 * 60 * 1000)]
  [Arguments(PerspectiveWriteMode.PerStream)]
  [Arguments(PerspectiveWriteMode.SetBased)]
  public async Task Measure_FanOutOfNewStreamsAsync(PerspectiveWriteMode mode, CancellationToken ct) {
    var count = int.TryParse(Environment.GetEnvironmentVariable("WHIZBANG_FANOUT_STREAMS"), out var configured) ? configured : 100_000;
    await using var conn = await _openAsync(ct);
    var probe = _composePipeline(mode);
    await probe.RegisterAsync(conn, ct);
    var instance = probe.InstanceProvider;
    await probe.DisposeAsync();

    // The completion path a service runs: the default (batched) completion strategy and the completion flush worker.
    var completion = new CountingCompletionChannel(count);
    await using var pipeline = _composePipeline(mode, instance, completion, consumers: 4, claimBatch: 1000, productionCompletion: true);
    var flusher = new PerspectiveCompletionFlushWorker(
      pipeline.Services.GetRequiredService<IServiceScopeFactory>(),
      Microsoft.Extensions.Options.Options.Create(new PerspectiveCompletionFlushWorkerOptions()),
      Microsoft.Extensions.Options.Options.Create(new WorkCoordinatorOptions()),
      Microsoft.Extensions.Logging.Abstractions.NullLogger<PerspectiveCompletionFlushWorker>.Instance,
      NoOpPinnedConnectionPool.Instance);
    completion.Inner = flusher;
    await flusher.StartAsync(ct);
    var streams = Enumerable.Range(0, count).Select(_ => (Guid)TrackedGuid.New()).ToList();
    for (var i = 0; i < streams.Count; i += 500) {
      await _commitManyAsync(conn, instance, [.. streams.Skip(i).Take(500)], ct);
    }

    using var statements = new NpgsqlStatementLog(ConnectionString);
    var clock = Stopwatch.StartNew(); // review-checks:allow: a measurement records elapsed time; nothing waits on it or asserts it
    await pipeline.HandOffAsync([], ct);
    await pipeline.StartWorkerAsync(ct);
    await completion.All.WaitAsync(TimeSpan.FromHours(2), ct);
    clock.Stop();
    await flusher.StopAsync(ct);

    var touching = statements.Matching(TABLE);
    var line = $"MEASURE mode={mode} streams={count} seconds={clock.Elapsed.TotalSeconds:F1} rows_per_second={count / clock.Elapsed.TotalSeconds:F0} "
      + $"table_statements={touching.Count} table_inserts={touching.Count(s => s.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))} "
      + $"total_statements={statements.Statements.Count}";
    var report = new List<string> { line };
    // What the statements were, by shape, most frequent first: where the round trips that remain go.
    foreach (var shape in statements.Statements
        .Select(text => _whitespace().Replace(text, " ").Trim())
        .Select(text => text.Length > 90 ? text[..90] : text)
        .GroupBy(text => text, StringComparer.Ordinal)
        .OrderByDescending(group => group.Count())
        .Take(12)) {
      report.Add($"MEASURE   mode={mode} count={shape.Count()} statement={shape.Key}");
    }
    // Standard output is captured per test, so the report also goes to a file a runner can read back.
    foreach (var reported in report) {
      Console.WriteLine(reported);
    }
    var reportPath = Environment.GetEnvironmentVariable("WHIZBANG_MEASURE_REPORT") is { Length: > 0 } named
      ? named
      : Path.Combine(AppContext.BaseDirectory, "fan-out-measurement.txt");
    await File.AppendAllLinesAsync(reportPath, report, ct);
    await Assert.That(await _countAsync(conn, ct)).IsGreaterThanOrEqualTo(count);
  }

  [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
  private static partial System.Text.RegularExpressions.Regex _whitespace();

  private static async Task<long> _countAsync(NpgsqlConnection conn, CancellationToken ct) {
    await using var command = conn.CreateCommand();
    command.CommandText = $"SELECT count(*) FROM {TABLE}";
    return (long)(await command.ExecuteScalarAsync(ct))!;
  }

  /// <summary>Commits one order event on each stream, in one call, and stamps them.</summary>
  private static async Task _commitManyAsync(
      NpgsqlConnection conn, ServiceInstanceProvider instance, List<Guid> streams, CancellationToken ct) {
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var messages = new StringBuilder();
    foreach (var streamId in streams) {
      var eventId = (Guid)TrackedGuid.New();
      var envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(eventId),
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        Hops = [],
        Payload = JsonSerializer.SerializeToElement(
          new SampleOrderCreatedEvent { OrderId = new TestOrderId(streamId), Amount = 1 }, jsonOptions.GetTypeInfo(typeof(SampleOrderCreatedEvent))),
      };
      var envelopeJson = JsonSerializer.Serialize(envelope, jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));
      if (messages.Length > 0) {
        messages.Append(',');
      }
      messages.Append(System.Globalization.CultureInfo.InvariantCulture, $$"""
        {"MessageId":"{{eventId}}","Destination":"order-events","MessageType":"{{TypeNameFormatter.Format(typeof(SampleOrderCreatedEvent))}}","EnvelopeType":null,"Envelope":{{envelopeJson}},"Metadata":{},"Scope":null,"StreamId":"{{streamId}}","IsEvent":true}
        """);
    }
    var request = $$"""
      {"instance_id":"{{instance.InstanceId}}","service_name":"{{instance.ServiceName}}","host_name":"{{instance.HostName}}","process_id":{{instance.ProcessId}},"new_outbox_messages":[{{messages}}]}
      """;
    await using (var call = conn.CreateCommand()) {
      call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
      call.Parameters.AddWithValue("req", request);
      _ = await call.ExecuteScalarAsync(ct);
    }
    await using var stamp = conn.CreateCommand();
    stamp.CommandText = "SELECT stamp_pending_commit_sequences(100000, false)";
    _ = await stamp.ExecuteScalarAsync(ct);
  }

  /// <summary>
  /// Passes every completion to the completion flush worker, and signals once every work row of the run has been
  /// reported complete.
  /// </summary>
  private sealed class CountingCompletionChannel(int expected) : IPerspectiveCompletionChannel {
    private readonly Lock _lock = new();
    private readonly HashSet<Guid> _seen = [];
    private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IPerspectiveCompletionChannel? Inner { get; set; }

    public Task All => _all.Task;

    public async ValueTask EnqueueEventWorkIdAsync(Guid eventWorkId, CancellationToken cancellationToken = default) {
      await Inner!.EnqueueEventWorkIdAsync(eventWorkId, cancellationToken);
      lock (_lock) {
        _seen.Add(eventWorkId);
        if (_seen.Count >= expected) {
          _all.TrySetResult();
        }
      }
    }

    public ValueTask EnqueueCursorAsync(PerspectiveCursorCompletion cursor, CancellationToken cancellationToken = default) =>
      Inner!.EnqueueCursorAsync(cursor, cancellationToken);
  }
}
