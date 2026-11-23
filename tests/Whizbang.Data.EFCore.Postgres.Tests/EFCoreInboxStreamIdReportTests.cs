// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Serialization;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Validation;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A non-v7 stream id that reaches this service only through the inbox, EF Core driver (#2549). The inbox row carries
/// the message type as a stored name; the write resolves it to the type, and through the type's <c>[StreamId]</c>
/// decides whether the stream id is a strong id that accepts any version. Otherwise the stream id is reported once per
/// stream and counted in <c>whizbang.guid_ordering.non_v7_stream_ids</c> by default, and refused at
/// <see cref="GuidOrderingSeverity.Error"/> before any SQL runs, so nothing of the batch is stored. A name the registry
/// does not know is reported, never skipped.
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#time-ordering-checks</docs>
[Category("Shard5")]
public class EFCoreInboxStreamIdReportTests : EFCoreTestBase {
  private const string ENTRY_POINT = "EFCoreWorkCoordinator.InboxWrite";
  private static readonly string _strictName = TypeNameFormatter.Format(typeof(EFCoreStrictStreamInboxNoted));
  private static readonly string _anyVersionName = TypeNameFormatter.Format(typeof(EFCoreAnyVersionStreamInboxNoted));

  [Test]
  public async Task NonV7StreamId_ByDefault_IsStoredReportedOnceAndCountedAsync() {
    using var probe = new GuidOrderingProbe(GuidOrderingSeverity.Warning);
    var stream = _uniqueNonV7Stream();
    var first = (Guid)TrackedGuid.New();
    var second = (Guid)TrackedGuid.New();

    await _coordinator(probe.Validator).StoreInboxMessagesAsync([_inbox(first, stream, _strictName)], partitionCount: 100);
    await _coordinator(probe.Validator).StoreInboxMessagesAsync([_inbox(second, stream, _strictName)], partitionCount: 100);

    await Assert.That(await _countAsync("SELECT count(*) FROM wh_inbox WHERE stream_id = @id", stream)).IsEqualTo(2L);
    await Assert.That(probe.StreamIdCount(ENTRY_POINT)).IsEqualTo(1L).Because("once per stream");
    await Assert.That(probe.Warnings).Count().IsEqualTo(1);
    await Assert.That(probe.Warnings[0]).Contains(stream.ToString()).And.Contains(_strictName).And.Contains(ENTRY_POINT);
  }

  [Test]
  public async Task NonV7StreamId_AtError_IsRefusedAndNothingOfTheBatchIsStoredAsync() {
    using var probe = new GuidOrderingProbe(GuidOrderingSeverity.Error);
    var goodStream = (Guid)TrackedGuid.New();
    var badStream = _uniqueNonV7Stream();
    var good = (Guid)TrackedGuid.New();
    var bad = (Guid)TrackedGuid.New();

    var thrown = await Assert.That(async () => await _coordinator(probe.Validator).StoreInboxMessagesAsync(
        [_inbox(good, goodStream, _strictName), _inbox(bad, badStream, _strictName)], partitionCount: 100))
      .Throws<GuidOrderingException>();

    await Assert.That(thrown!.Id).IsEqualTo(badStream);
    await Assert.That(thrown.EntryPoint).IsEqualTo(ENTRY_POINT);
    await _assertNothingStoredAsync(goodStream, good);
    await _assertNothingStoredAsync(badStream, bad);
  }

  [Test]
  [Arguments(GuidOrderingSeverity.Error)]
  [Arguments(GuidOrderingSeverity.Warning)]
  public async Task AnyVersionStrongStreamId_IsStoredAndNeverReportedAsync(GuidOrderingSeverity severity) {
    using var probe = new GuidOrderingProbe(severity);
    var stream = _uniqueNonV7Stream();

    await _coordinator(probe.Validator).StoreInboxMessagesAsync(
      [_inbox((Guid)TrackedGuid.New(), stream, _anyVersionName)], partitionCount: 100);

    await Assert.That(await _countAsync("SELECT count(*) FROM wh_inbox WHERE stream_id = @id", stream)).IsEqualTo(1L);
    await Assert.That(probe.StreamIdCount(ENTRY_POINT)).IsEqualTo(0L);
    await Assert.That(probe.Warnings).IsEmpty();
  }

  [Test]
  public async Task UnresolvableMessageType_IsReportedNotSkippedAsync() {
    using var probe = new GuidOrderingProbe(GuidOrderingSeverity.Error);
    var stream = _uniqueNonV7Stream();
    var id = (Guid)TrackedGuid.New();

    var thrown = await Assert.That(async () => await _coordinator(probe.Validator).StoreInboxMessagesAsync(
        [_inbox(id, stream, "Nowhere.NotAMessage, Nowhere")], partitionCount: 100))
      .Throws<GuidOrderingException>();

    await Assert.That(thrown!.MessageType).IsEqualTo("Nowhere.NotAMessage, Nowhere");
    await _assertNothingStoredAsync(stream, id);
  }

  // A distinct v4 stream per test, so the once-per-stream memory of one test never hides another's report
  private static Guid _uniqueNonV7Stream() {
    Span<byte> bytes = stackalloc byte[16];
    Guid.NewGuid().TryWriteBytes(bytes);
    bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
    return new Guid(bytes);
  }

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(GuidOrderingValidator validator) =>
    new(_context, JsonContextRegistry.CreateCombinedOptions(), guidOrdering: validator);

  private WorkCoordinationDbContext? _opened;

  private WorkCoordinationDbContext _context => _opened ??= CreateDbContext();

  [After(Test)]
  public async Task CloseContextAsync() {
    if (_opened is not null) {
      await _opened.DisposeAsync();
    }
  }

  private async Task _assertNothingStoredAsync(Guid stream, Guid id) {
    await Assert.That(await _countAsync("SELECT count(*) FROM wh_inbox WHERE message_id = @id OR stream_id = @stream", id, stream))
      .IsEqualTo(0L).Because("no inbox row");
    await Assert.That(await _countAsync("SELECT count(*) FROM wh_event_store WHERE event_id = @id OR stream_id = @stream", id, stream))
      .IsEqualTo(0L).Because("no event row");
    await Assert.That(await _countAsync("SELECT count(*) FROM wh_active_streams WHERE stream_id = @stream", id, stream))
      .IsEqualTo(0L).Because("no stream row");
    await Assert.That(await _countAsync("SELECT count(*) FROM wh_perspective_cursors WHERE stream_id = @stream", id, stream))
      .IsEqualTo(0L).Because("no cursor");
  }

  private async Task<long> _countAsync(string sql, Guid id, Guid? stream = null) {
    var conn = (NpgsqlConnection)_context.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue(nameof(id), id);
    cmd.Parameters.AddWithValue(nameof(stream), stream ?? id);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private static InboxMessage _inbox(Guid id, Guid stream, string messageType) => new() {
    MessageId = id,
    HandlerName = "ProbeHandler",
    Envelope = new MessageEnvelope<JsonElement> {
      MessageId = new MessageId(id),
      DispatchContext = new MessageDispatchContext { Mode = Whizbang.Core.Dispatch.DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = [],
      Payload = JsonElement.Parse("{}"),
    },
    EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
    MessageType = messageType,
    StreamId = stream,
    IsEvent = true,
    Metadata = new EnvelopeMetadata { MessageId = new MessageId(id), Hops = [] },
  };
}

/// <summary>An event whose stream id is a plain Guid, held to the UUIDv7 report (#2549).</summary>
public sealed record EFCoreStrictStreamInboxNoted : IEvent {
  /// <summary>The stream.</summary>
  [StreamId]
  public required Guid StreamId { get; init; }
}

/// <summary>An event whose stream id is a strong id that accepts any version (#2549).</summary>
public sealed record EFCoreAnyVersionStreamInboxNoted : IEvent {
  /// <summary>The stream.</summary>
  [StreamId]
  public required EFCoreAnyVersionStreamKey StreamId { get; init; }
}

/// <summary>A strong stream id that takes any non-empty Guid version (#2549).</summary>
[WhizbangId(AcceptAnyVersion = true)]
public readonly partial struct EFCoreAnyVersionStreamKey;

/// <summary>A validator with its own logger and counters, read back without any other test's measurements.</summary>
internal sealed class GuidOrderingProbe : IDisposable {
  private readonly ProbeMeterFactory _factory = new();
  private readonly MeterListener _listener = new();
  private readonly List<(string EntryPoint, long Value)> _streamIds = [];
  private readonly WarningLogger _logger = new();

  public GuidOrderingProbe(GuidOrderingSeverity streamSeverity) {
    Validator = new GuidOrderingValidator(new WhizbangOptions { StreamIdOrderingViolationSeverity = streamSeverity },
      _logger, new GuidOrderingMetrics(new WhizbangMetrics(_factory)));
    _listener.InstrumentPublished = (instrument, listener) => {
      if (_factory.Created.Contains(instrument.Meter) && instrument.Name == GuidOrderingMetrics.NON_V7_STREAM_IDS) {
        listener.EnableMeasurementEvents(instrument);
      }
    };
    _listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      var entryPoint = "";
      foreach (var tag in tags) {
        if (tag.Key == GuidOrderingMetrics.ENTRY_POINT_TAG) {
          entryPoint = tag.Value as string ?? "";
        }
      }
      lock (_streamIds) {
        _streamIds.Add((entryPoint, value));
      }
    });
    _listener.Start();
  }

  public GuidOrderingValidator Validator { get; }

  public List<string> Warnings => _logger.Warnings;

  public long StreamIdCount(string entryPoint) {
    lock (_streamIds) {
      return _streamIds.Where(m => m.EntryPoint == entryPoint).Sum(m => m.Value);
    }
  }

  public void Dispose() {
    _listener.Dispose();
    _factory.Dispose();
  }

  private sealed class ProbeMeterFactory : IMeterFactory {
    public List<Meter> Created { get; } = [];

    public Meter Create(MeterOptions options) {
      var meter = new Meter(options);
      Created.Add(meter);
      return meter;
    }

    public void Dispose() {
      foreach (var meter in Created) {
        meter.Dispose();
      }
    }
  }

  private sealed class WarningLogger : ILogger {
    public List<string> Warnings { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      if (logLevel == LogLevel.Warning) {
        Warnings.Add(formatter(state, exception));
      }
    }
  }
}
