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
/// The stream count is <c>WHIZBANG_FANOUT_STREAMS</c> (100,000 by default), and <c>WHIZBANG_MEASURE_REPORT</c> names a
/// file the results are appended to. Completions are written to the coordinator
/// in batches, as the completion flush worker writes them, so the harness does not add a round trip per row to either
/// mode. The results are written to standard output (and the report file), one <c>MEASURE</c> line per mode.
/// </remarks>
public partial class SetBasedPerspectiveWorkerIntegrationTests {
  [Test]
  [Explicit]
  [Arguments(PerspectiveWriteMode.PerStream)]
  [Arguments(PerspectiveWriteMode.SetBased)]
  public async Task Measure_FanOutOfNewStreamsAsync(PerspectiveWriteMode mode) {
    var ct = CancellationToken.None;
    var count = int.TryParse(Environment.GetEnvironmentVariable("WHIZBANG_FANOUT_STREAMS"), out var configured) ? configured : 100_000;
    await using var conn = await _openAsync(ct);
    var probe = _composePipeline(mode);
    await probe.RegisterAsync(conn, ct);
    var instance = probe.InstanceProvider;
    await probe.DisposeAsync();

    var completion = new BatchingCompletionChannel(count);
    await using var pipeline = _composePipeline(mode, instance, completion, consumers: 4, claimBatch: 1000);
    completion.ScopeFactory = pipeline.Services.GetRequiredService<IServiceScopeFactory>();
    var streams = Enumerable.Range(0, count).Select(_ => (Guid)TrackedGuid.New()).ToList();
    for (var i = 0; i < streams.Count; i += 500) {
      await _commitManyAsync(conn, instance, streams.Skip(i).Take(500).ToList(), ct);
    }

    using var statements = new NpgsqlStatementLog(ConnectionString);
    var clock = Stopwatch.StartNew();
    await pipeline.HandOffAsync([], ct);
    await pipeline.StartWorkerAsync(ct);
    await completion.All.WaitAsync(TimeSpan.FromHours(2), ct);
    clock.Stop();
    await completion.FlushAsync(ct);

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
    var reportPath = Environment.GetEnvironmentVariable("WHIZBANG_MEASURE_REPORT");
    if (!string.IsNullOrEmpty(reportPath)) {
      await File.AppendAllLinesAsync(reportPath, report, ct);
    }
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
  /// Counts completed work rows, signaling when all of them are in, and writes them to the coordinator in batches of
  /// 500, as the completion flush worker does.
  /// </summary>
  private sealed class BatchingCompletionChannel(int expected) : IPerspectiveCompletionChannel {
    private readonly Lock _lock = new();
    private readonly List<Guid> _workIds = [];
    private readonly List<PerspectiveCursorCompletion> _cursors = [];
    private readonly HashSet<Guid> _seen = [];
    private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IServiceScopeFactory? ScopeFactory { get; set; }

    public Task All => _all.Task;

    public async ValueTask EnqueueEventWorkIdAsync(Guid eventWorkId, CancellationToken cancellationToken = default) {
      bool flush;
      lock (_lock) {
        _workIds.Add(eventWorkId);
        _seen.Add(eventWorkId);
        flush = _workIds.Count >= 500;
        if (_seen.Count >= expected) {
          _all.TrySetResult();
        }
      }
      if (flush) {
        await FlushAsync(cancellationToken);
      }
    }

    public ValueTask EnqueueCursorAsync(PerspectiveCursorCompletion cursor, CancellationToken cancellationToken = default) {
      lock (_lock) {
        _cursors.Add(cursor);
      }
      return ValueTask.CompletedTask;
    }

    public async Task FlushAsync(CancellationToken ct) {
      Guid[] workIds;
      PerspectiveCursorCompletion[] cursors;
      lock (_lock) {
        workIds = [.. _workIds];
        cursors = [.. _cursors];
        _workIds.Clear();
        _cursors.Clear();
      }
      if (workIds.Length == 0 && cursors.Length == 0) {
        return;
      }
      await using var scope = ScopeFactory!.CreateAsyncScope();
      await scope.ServiceProvider.GetRequiredService<IWorkCoordinator>().CompletePerspectiveAsync(cursors, workIds, debugMode: false, ct);
    }
  }
}
