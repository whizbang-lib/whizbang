// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Configuration;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Tests.ValueObjects;
using Whizbang.Core.Validation;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Validation;

/// <summary>
/// A stream id whose message type is known only by its stored name, as on an inbox write (#2549): the name is resolved
/// to the message type through the registry the generated JSON contexts fill, with no reflection, and the type's
/// <c>[StreamId]</c> says whether the stream id is a strong id that accepts any version. Such an id is never reported;
/// any other non-v7 stream id is reported once and counted, or refused at <see cref="GuidOrderingSeverity.Error"/>, and
/// a name the registry does not know is reported, never skipped.
/// </summary>
[Category("Validation")]
[Category("GuidOrdering")]
public class GuidOrderingStreamIdByNameTests {
  private const string ENTRY_POINT = "Test.InboxWrite";
  private const string UNKNOWN_TYPE = "Nowhere.NotAMessage, Nowhere";

  private static readonly string _anyVersionName = TypeNameFormatter.Format(typeof(AnyVersionStreamProbeNoted));
  private static readonly string _strictName = TypeNameFormatter.Format(typeof(StrictStreamProbeNoted));

  private static (GuidOrderingValidator Validator, CollectingLogger Logger, System.Diagnostics.Metrics.Meter Meter, TestMeterFactory Factory)
      _validator(GuidOrderingSeverity streamSeverity) {
    var factory = new TestMeterFactory();
    var metrics = new GuidOrderingMetrics(new WhizbangMetrics(factory));
    var logger = new CollectingLogger();
    var options = new WhizbangOptions { StreamIdOrderingViolationSeverity = streamSeverity };
    return (new GuidOrderingValidator(options, logger, metrics), logger, factory.CreatedMeters[0], factory);
  }

  [Test]
  [Arguments(GuidOrderingSeverity.Error)]
  [Arguments(GuidOrderingSeverity.Warning)]
  public async Task AnyVersionStrongId_ResolvedByName_IsNeverReportedAsync(GuidOrderingSeverity streamSeverity) {
    var (validator, logger, meter, factory) = _validator(streamSeverity);
    using (factory) {
      validator.ReportStreamId(NonTimeOrderedGuidShapes.V4, _anyVersionName, ENTRY_POINT);

      await Assert.That(logger.Entries).IsEmpty();
      await Assert.That(ProbeMeterReader.ReadTotal(meter, GuidOrderingMetrics.NON_V7_STREAM_IDS)).IsEqualTo(0);
    }
  }

  [Test]
  public async Task StrictStreamId_ResolvedByName_IsReportedOnceNamingTheTypeAndCountedAsync() {
    var (validator, logger, meter, factory) = _validator(GuidOrderingSeverity.Warning);
    using (factory) {
      validator.ReportStreamId(NonTimeOrderedGuidShapes.V5, _strictName, ENTRY_POINT);
      validator.ReportStreamId(NonTimeOrderedGuidShapes.V5, _strictName, ENTRY_POINT);

      await Assert.That(logger.Entries).Count().IsEqualTo(1);
      await Assert.That(logger.Entries[0].Level).IsEqualTo(LogLevel.Warning);
      await Assert.That(logger.Entries[0].Message).Contains(NonTimeOrderedGuidShapes.V5.ToString())
        .And.Contains(_strictName).And.Contains(ENTRY_POINT);
      await Assert.That(ProbeMeterReader.ReadTotal(meter, GuidOrderingMetrics.NON_V7_STREAM_IDS)).IsEqualTo(1);
    }
  }

  [Test]
  public async Task StrictStreamId_ResolvedByName_IsRefusedAtErrorAsync() {
    var (validator, _, _, factory) = _validator(GuidOrderingSeverity.Error);
    using (factory) {
      var thrown = Assert.Throws<GuidOrderingException>(
        () => validator.ReportStreamId(NonTimeOrderedGuidShapes.V8, _strictName, ENTRY_POINT));

      await Assert.That(thrown.Id).IsEqualTo(NonTimeOrderedGuidShapes.V8);
      await Assert.That(thrown.EntryPoint).IsEqualTo(ENTRY_POINT);
      await Assert.That(thrown.MessageType).IsEqualTo(_strictName);
    }
  }

  [Test]
  public async Task UnresolvableName_IsReportedNotSkippedAsync() {
    var (validator, logger, meter, factory) = _validator(GuidOrderingSeverity.Warning);
    using (factory) {
      validator.ReportStreamId(NonTimeOrderedGuidShapes.V4, UNKNOWN_TYPE, ENTRY_POINT);

      await Assert.That(logger.Entries).Count().IsEqualTo(1);
      await Assert.That(logger.Entries[0].Message).Contains(UNKNOWN_TYPE);
      await Assert.That(ProbeMeterReader.ReadTotal(meter, GuidOrderingMetrics.NON_V7_STREAM_IDS)).IsEqualTo(1);
    }
  }

  [Test]
  public async Task UnresolvableName_IsRefusedAtErrorAsync() {
    var (validator, _, _, factory) = _validator(GuidOrderingSeverity.Error);
    using (factory) {
      var thrown = Assert.Throws<GuidOrderingException>(
        () => validator.ReportStreamId(NonTimeOrderedGuidShapes.V4, UNKNOWN_TYPE, ENTRY_POINT));

      await Assert.That(thrown.MessageType).IsEqualTo(UNKNOWN_TYPE);
    }
  }

  [Test]
  [Arguments(GuidOrderingSeverity.Error)]
  [Arguments(GuidOrderingSeverity.Warning)]
  public async Task V7AndEmpty_AreNeverReportedByNameAsync(GuidOrderingSeverity streamSeverity) {
    var (validator, logger, meter, factory) = _validator(streamSeverity);
    using (factory) {
      validator.ReportStreamId(TrackedGuid.New(), UNKNOWN_TYPE, ENTRY_POINT);
      validator.ReportStreamId(Guid.Empty, UNKNOWN_TYPE, ENTRY_POINT);

      await Assert.That(logger.Entries).IsEmpty();
      await Assert.That(ProbeMeterReader.ReadTotal(meter, GuidOrderingMetrics.NON_V7_STREAM_IDS)).IsEqualTo(0);
    }
  }

  [Test]
  public async Task TheRegistry_ResolvesAStoredNameToItsTypeAsync() {
    var resolved = Whizbang.Core.Serialization.JsonContextRegistry.TryGetTypeByName(_strictName, out var type);

    await Assert.That(resolved).IsTrue();
    await Assert.That(type).IsEqualTo(typeof(StrictStreamProbeNoted));
  }

  [Test]
  [Arguments("")]
  [Arguments(UNKNOWN_TYPE)]
  public async Task TheRegistry_DoesNotResolveAnEmptyOrUnknownNameAsync(string name) {
    var resolved = Whizbang.Core.Serialization.JsonContextRegistry.TryGetTypeByName(name, out var type);

    await Assert.That(resolved).IsFalse();
    await Assert.That(type).IsNull();
  }

  private sealed class CollectingLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
  }
}

/// <summary>An event whose stream id is a strong id that accepts any version (#2549).</summary>
public sealed record AnyVersionStreamProbeNoted : IEvent {
  /// <summary>The stream.</summary>
  [StreamId]
  public required AnyVersionTestId StreamId { get; init; }
}

/// <summary>An event whose stream id is a plain Guid, held to the UUIDv7 report (#2549).</summary>
public sealed record StrictStreamProbeNoted : IEvent {
  /// <summary>The stream.</summary>
  [StreamId]
  public required Guid StreamId { get; init; }
}
