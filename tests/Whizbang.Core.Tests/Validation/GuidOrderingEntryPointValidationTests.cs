// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Configuration;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Validation;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Validation;

/// <summary>
/// The check every event and message id entry point runs (#2528): a non-v7 id is refused by default, naming the id, the
/// message type, the entry point and the version; <see cref="Guid.Empty"/> is always refused; at
/// <see cref="GuidOrderingSeverity.Warning"/> each non-v7 id is logged and counted once. A non-v7 stream id is reported,
/// not refused, unless raised to <see cref="GuidOrderingSeverity.Error"/>, and an id type that accepts any version is
/// never reported.
/// </summary>
[Category("Validation")]
[Category("GuidOrdering")]
public class GuidOrderingEntryPointValidationTests {
  private const string MESSAGE_TYPE = "App.Contracts.OrderPlaced";
  private const string ENTRY_POINT = "Test.EntryPoint";

  /// <summary>Every non-v7 shape the issue names, with the version a reader would see.</summary>
  public static IEnumerable<Func<(Guid Id, int Version)>> NonV7Ids() {
    foreach (var id in NonTimeOrderedGuidShapes.All) {
      yield return () => (id, id.Version);
    }
  }

  private static (GuidOrderingValidator Validator, CollectingLogger Logger, System.Diagnostics.Metrics.Meter Meter, TestMeterFactory Factory)
      _validator(GuidOrderingSeverity severity, GuidOrderingSeverity streamSeverity = GuidOrderingSeverity.Warning) {
    var factory = new TestMeterFactory();
    var metrics = new GuidOrderingMetrics(new WhizbangMetrics(factory));
    var logger = new CollectingLogger();
    var options = new WhizbangOptions {
      GuidOrderingViolationSeverity = severity,
      StreamIdOrderingViolationSeverity = streamSeverity
    };
    return (new GuidOrderingValidator(options, logger, metrics), logger, factory.CreatedMeters[0], factory);
  }

  [Test]
  public async Task Default_SeverityIsError_AndTheStreamSeverityIsWarningAsync() {
    var options = new WhizbangOptions();

    await Assert.That(options.GuidOrderingViolationSeverity).IsEqualTo(GuidOrderingSeverity.Error);
    await Assert.That(options.StreamIdOrderingViolationSeverity).IsEqualTo(GuidOrderingSeverity.Warning);
  }

  [Test]
  [MethodDataSource(nameof(NonV7Ids))]
  public async Task ValidateMessageId_NonV7AtError_ThrowsNamingIdTypeEntryPointAndVersionAsync(Guid id, int version) {
    var (validator, _, _, factory) = _validator(GuidOrderingSeverity.Error);
    using (factory) {
      var thrown = Assert.Throws<GuidOrderingException>(() => validator.ValidateMessageId(id, MESSAGE_TYPE, ENTRY_POINT));

      await Assert.That(thrown.Id).IsEqualTo(id);
      await Assert.That(thrown.MessageType).IsEqualTo(MESSAGE_TYPE);
      await Assert.That(thrown.EntryPoint).IsEqualTo(ENTRY_POINT);
      await Assert.That(thrown.Version).IsEqualTo(version);
      await Assert.That(thrown.Message).Contains(id.ToString()).And.Contains(MESSAGE_TYPE).And.Contains(ENTRY_POINT)
        .And.Contains($"version {version}");
    }
  }

  [Test]
  public async Task ValidateMessageId_TheDefaultValidator_RefusesAV4Async() {
    var thrown = Assert.Throws<GuidOrderingException>(
      () => GuidOrderingValidator.Default.ValidateMessageId(NonTimeOrderedGuidShapes.V4, MESSAGE_TYPE, ENTRY_POINT));

    await Assert.That(thrown.Version).IsEqualTo(4);
  }

  [Test]
  [Arguments(GuidOrderingSeverity.Error)]
  [Arguments(GuidOrderingSeverity.Warning)]
  [Arguments(GuidOrderingSeverity.Info)]
  [Arguments(GuidOrderingSeverity.None)]
  public async Task ValidateMessageId_V7_IsAcceptedSilentlyAtEverySeverityAsync(GuidOrderingSeverity severity) {
    var (validator, logger, meter, factory) = _validator(severity);
    using (factory) {
      validator.ValidateMessageId(TrackedGuid.New(), MESSAGE_TYPE, ENTRY_POINT);

      await Assert.That(logger.Entries).IsEmpty();
      await Assert.That(ProbeMeterReader.ReadTotal(meter, GuidOrderingMetrics.NON_V7_MESSAGE_IDS)).IsEqualTo(0);
    }
  }

  [Test]
  [Arguments(GuidOrderingSeverity.Warning)]
  [Arguments(GuidOrderingSeverity.Info)]
  [Arguments(GuidOrderingSeverity.None)]
  public async Task ValidateMessageId_EmptyIsRefusedAtEverySeverityAsync(GuidOrderingSeverity severity) {
    var (validator, _, _, factory) = _validator(severity);
    using (factory) {
      var thrown = Assert.Throws<GuidOrderingException>(() => validator.ValidateMessageId(Guid.Empty, MESSAGE_TYPE, ENTRY_POINT));

      await Assert.That(thrown.Version).IsEqualTo(0);
      await Assert.That(thrown.Message).Contains("empty");
    }
  }

  [Test]
  public async Task ValidateMessageId_AtWarning_LogsAndCountsEachIdOnceAsync() {
    var (validator, logger, meter, factory) = _validator(GuidOrderingSeverity.Warning);
    using (factory) {
      validator.ValidateMessageId(NonTimeOrderedGuidShapes.V4, MESSAGE_TYPE, ENTRY_POINT);
      validator.ValidateMessageId(NonTimeOrderedGuidShapes.V4, MESSAGE_TYPE, ENTRY_POINT);
      validator.ValidateMessageId(NonTimeOrderedGuidShapes.V5, MESSAGE_TYPE, ENTRY_POINT);

      await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning)).IsEqualTo(2)
        .Because("each non-v7 id is logged once, however often it is seen");
      await Assert.That(logger.Entries[0].Message).Contains(NonTimeOrderedGuidShapes.V4.ToString())
        .And.Contains(MESSAGE_TYPE).And.Contains(ENTRY_POINT).And.Contains("4");
      await Assert.That(ProbeMeterReader.ReadTotal(meter, GuidOrderingMetrics.NON_V7_MESSAGE_IDS)).IsEqualTo(2);
      var series = ProbeMeterReader.ReadSeries(meter, GuidOrderingMetrics.NON_V7_MESSAGE_IDS);
      await Assert.That(series.All(s => (string?)s.Tags[GuidOrderingMetrics.ENTRY_POINT_TAG] == ENTRY_POINT)).IsTrue();
    }
  }

  [Test]
  public async Task ValidateMessageId_AtInfo_LogsAtInformationAsync() {
    var (validator, logger, meter, factory) = _validator(GuidOrderingSeverity.Info);
    using (factory) {
      validator.ValidateMessageId(NonTimeOrderedGuidShapes.V4, MESSAGE_TYPE, ENTRY_POINT);

      await Assert.That(logger.Entries.Single().Level).IsEqualTo(LogLevel.Information);
      await Assert.That(ProbeMeterReader.ReadTotal(meter, GuidOrderingMetrics.NON_V7_MESSAGE_IDS)).IsEqualTo(1);
    }
  }

  [Test]
  public async Task ValidateMessageId_AtNone_AcceptsSilentlyAsync() {
    var (validator, logger, meter, factory) = _validator(GuidOrderingSeverity.None);
    using (factory) {
      validator.ValidateMessageId(NonTimeOrderedGuidShapes.V4, MESSAGE_TYPE, ENTRY_POINT);

      await Assert.That(logger.Entries).IsEmpty();
      await Assert.That(ProbeMeterReader.ReadTotal(meter, GuidOrderingMetrics.NON_V7_MESSAGE_IDS)).IsEqualTo(0);
    }
  }

  [Test]
  public async Task ValidateMessageId_WithoutMetrics_StillLogsAtWarningAsync() {
    var logger = new CollectingLogger();
    var validator = new GuidOrderingValidator(new WhizbangOptions { GuidOrderingViolationSeverity = GuidOrderingSeverity.Warning }, logger);

    validator.ValidateMessageId(NonTimeOrderedGuidShapes.V4, MESSAGE_TYPE, ENTRY_POINT);

    await Assert.That(logger.Entries).Count().IsEqualTo(1);
  }

  [Test]
  public async Task ValidateMessageId_TheOncePerIdMemoryIsBounded_AndForgetsWhenFullAsync() {
    var (validator, logger, _, factory) = _validator(GuidOrderingSeverity.Warning);
    using (factory) {
      validator.ValidateMessageId(NonTimeOrderedGuidShapes.V4, MESSAGE_TYPE, ENTRY_POINT);
      for (var i = 0; i < GuidOrderingValidator.MAX_REMEMBERED_IDS; i++) {
        validator.ValidateMessageId(_v4(i), MESSAGE_TYPE, ENTRY_POINT);
      }
      var before = logger.Entries.Count;
      validator.ValidateMessageId(NonTimeOrderedGuidShapes.V4, MESSAGE_TYPE, ENTRY_POINT);

      await Assert.That(logger.Entries.Count).IsEqualTo(before + 1)
        .Because("the memory was cleared when it filled, so the first id is reported again rather than growing without bound");
    }
  }

  // ---- stream ids ----

  [Test]
  [MethodDataSource(nameof(NonV7Ids))]
  public async Task ReportStreamId_NonV7AtTheDefault_IsAcceptedLoggedOnceAndCountedAsync(Guid id, int version) {
    if (id == Guid.Empty) {
      return; // an empty stream id is StreamIdGuard's to refuse, never a report
    }
    var (validator, logger, meter, factory) = _validator(GuidOrderingSeverity.Error);
    using (factory) {
      validator.ReportStreamId(id, streamIdType: null, MESSAGE_TYPE, ENTRY_POINT);
      validator.ReportStreamId(id, streamIdType: null, MESSAGE_TYPE, ENTRY_POINT);

      await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning)).IsEqualTo(1);
      await Assert.That(logger.Entries[0].Message).Contains(id.ToString()).And.Contains(MESSAGE_TYPE).And.Contains(ENTRY_POINT)
        .And.Contains(version.ToString(System.Globalization.CultureInfo.InvariantCulture));
      await Assert.That(ProbeMeterReader.ReadTotal(meter, GuidOrderingMetrics.NON_V7_STREAM_IDS)).IsEqualTo(1);
    }
  }

  [Test]
  public async Task ReportStreamId_RaisedToError_IsRefusedAsync() {
    var (validator, _, _, factory) = _validator(GuidOrderingSeverity.Error, GuidOrderingSeverity.Error);
    using (factory) {
      var thrown = Assert.Throws<GuidOrderingException>(
        () => validator.ReportStreamId(NonTimeOrderedGuidShapes.V5, streamIdType: null, MESSAGE_TYPE, ENTRY_POINT));

      await Assert.That(thrown.Id).IsEqualTo(NonTimeOrderedGuidShapes.V5);
      await Assert.That(thrown.Message).Contains("stream id");
    }
  }

  [Test]
  [Arguments(GuidOrderingSeverity.Error)]
  [Arguments(GuidOrderingSeverity.Warning)]
  public async Task ReportStreamId_V7AndEmpty_AreNeverReportedAsync(GuidOrderingSeverity streamSeverity) {
    var (validator, logger, meter, factory) = _validator(GuidOrderingSeverity.Error, streamSeverity);
    using (factory) {
      validator.ReportStreamId(TrackedGuid.New(), streamIdType: null, MESSAGE_TYPE, ENTRY_POINT);
      validator.ReportStreamId(Guid.Empty, streamIdType: null, MESSAGE_TYPE, ENTRY_POINT);

      await Assert.That(logger.Entries).IsEmpty();
      await Assert.That(ProbeMeterReader.ReadTotal(meter, GuidOrderingMetrics.NON_V7_STREAM_IDS)).IsEqualTo(0);
    }
  }

  [Test]
  public async Task ReportStreamId_AtNone_IsSilentAsync() {
    var (validator, logger, _, factory) = _validator(GuidOrderingSeverity.Error, GuidOrderingSeverity.None);
    using (factory) {
      validator.ReportStreamId(NonTimeOrderedGuidShapes.V4, streamIdType: null, MESSAGE_TYPE, ENTRY_POINT);

      await Assert.That(logger.Entries).IsEmpty();
    }
  }

  [Test]
  public async Task ReportStreamId_AtInfo_LogsAtInformationAsync() {
    var (validator, logger, _, factory) = _validator(GuidOrderingSeverity.Error, GuidOrderingSeverity.Info);
    using (factory) {
      validator.ReportStreamId(NonTimeOrderedGuidShapes.V4, streamIdType: null, MESSAGE_TYPE, ENTRY_POINT);

      await Assert.That(logger.Entries.Single().Level).IsEqualTo(LogLevel.Information);
    }
  }

  [Test]
  [Arguments(GuidOrderingSeverity.Error)]
  [Arguments(GuidOrderingSeverity.Warning)]
  public async Task ReportStreamId_AnIdTypeThatAcceptsAnyVersion_IsNeverReportedAsync(GuidOrderingSeverity streamSeverity) {
    WhizbangIdProviderRegistry.RegisterAcceptsAnyVersion<AnyVersionStreamKey>();
    var (validator, logger, meter, factory) = _validator(GuidOrderingSeverity.Error, streamSeverity);
    using (factory) {
      validator.ReportStreamId(NonTimeOrderedGuidShapes.V4, typeof(AnyVersionStreamKey), MESSAGE_TYPE, ENTRY_POINT);

      await Assert.That(logger.Entries).IsEmpty();
      await Assert.That(ProbeMeterReader.ReadTotal(meter, GuidOrderingMetrics.NON_V7_STREAM_IDS)).IsEqualTo(0);
    }
  }

  [Test]
  public async Task ReportStreamId_AStrictIdType_IsReportedAsync() {
    var (validator, logger, _, factory) = _validator(GuidOrderingSeverity.Error);
    using (factory) {
      validator.ReportStreamId(NonTimeOrderedGuidShapes.V4, typeof(Guid), MESSAGE_TYPE, ENTRY_POINT);

      await Assert.That(logger.Entries).Count().IsEqualTo(1);
    }
  }

  [Test]
  public async Task GuidOrderingMetrics_IsInTheFrameworkMeterListAsync() {
    await Assert.That(WhizbangMeters.All).Contains(GuidOrderingMetrics.METER_NAME);
  }

  private static Guid _v4(int i) {
    Span<byte> bytes = stackalloc byte[16];
    BitConverter.TryWriteBytes(bytes, i + 1);
    bytes[7] = 0x40; // version 4 in the little-endian layout of System.Guid
    bytes[8] = 0x80;
    return new Guid(bytes);
  }

  /// <summary>A strong-id stand-in registered as accepting any version.</summary>
  private readonly struct AnyVersionStreamKey : IWhizbangId {
    public Guid ToGuid() => Guid.Empty;
    public bool IsTimeOrdered => false;
    public bool SubMillisecondPrecision => false;
    public DateTimeOffset Timestamp => default;
    public bool Equals(IWhizbangId? other) => false;
    public int CompareTo(IWhizbangId? other) => 0;
  }

  private sealed class CollectingLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
  }
}

/// <summary>The non-v7 shapes the refusal matrix covers (#2528).</summary>
internal static class NonTimeOrderedGuidShapes {
  public static readonly Guid V1 = Guid.Parse("c232ab00-9414-11ec-b3c8-9f6bdeced846");
  public static readonly Guid V3 = Guid.Parse("6fa459ea-ee8a-3ca4-894e-db77e160355e");
  public static readonly Guid V4 = Guid.Parse("919108f7-52d1-4320-9bac-f847db4148a8");
  public static readonly Guid V5 = Guid.Parse("886313e1-3b8a-5372-9b90-0c9aee199e5d");
  public static readonly Guid V6 = Guid.Parse("1ec9414c-232a-6b00-b3c8-9f6bdeced846");
  public static readonly Guid V8 = Guid.Parse("320c3d4d-cc00-875b-8ec9-32d5f69181c0");
  public static readonly Guid Max = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
  /// <summary>A raw hash with an arbitrary version nibble (11), the shape a truncated SHA-256 leaves.</summary>
  public static readonly Guid RawHash = Guid.Parse("a3f1c2d4-5e6f-b123-9abc-def012345678");

  public static IReadOnlyList<Guid> All { get; } = [V1, V3, V4, V5, V6, V8, Guid.Empty, Max, RawHash];
}
