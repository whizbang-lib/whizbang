// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Configuration;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Validation;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Validation;

/// <summary>
/// The stream id report runs on every event store append and every outbox write, so a time-ordered stream id must cost
/// nothing (#2548). The runtime caches a type's name behind a weak handle: a collection drops it and the next read
/// allocates again. Each measurement therefore collects inside its own window, so a path that reads the name before it
/// knows the id is reported fails on every run, not only when another test's collection happens to land there.
/// </summary>
[Category("Validation")]
[Category("GuidOrdering")]
[NotInParallel("AllocationMeasurement")]
public class GuidOrderingStreamIdByTypeTests {
  private const int ITERATIONS = 1_000;
  private const string ENTRY_POINT = "Test.Append";

  [Test]
  public async Task ValidateAppend_V7StreamAndEventIds_AllocatesNothingAsync() {
    var validator = new GuidOrderingValidator(new WhizbangOptions(), new CollectingLogger());
    var stream = TrackedGuid.New().Value;
    var envelope = new MessageEnvelope<StreamIdAllocationProbeNoted> {
      MessageId = MessageId.New(),
      Payload = new StreamIdAllocationProbeNoted(stream),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };
    validator.ValidateAppend(stream, envelope, ENTRY_POINT);

    var before = GC.GetAllocatedBytesForCurrentThread();
    GC.Collect();
    for (var i = 0; i < ITERATIONS; i++) {
      validator.ValidateAppend(stream, envelope, ENTRY_POINT);
    }
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

    await Assert.That(allocated).IsEqualTo(0L)
      .Because("the payload type's name is read only when the stream id is reported");
  }

  [Test]
  public async Task ReportStreamId_ByTypeWithAV7Id_AllocatesNothingAsync() {
    var validator = new GuidOrderingValidator(new WhizbangOptions(), new CollectingLogger());
    var stream = TrackedGuid.New().Value;
    validator.ReportStreamId(stream, streamIdType: null, typeof(StreamIdAllocationProbeNoted), ENTRY_POINT);

    var before = GC.GetAllocatedBytesForCurrentThread();
    GC.Collect();
    for (var i = 0; i < ITERATIONS; i++) {
      validator.ReportStreamId(stream, streamIdType: null, typeof(StreamIdAllocationProbeNoted), ENTRY_POINT);
    }
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

    await Assert.That(allocated).IsEqualTo(0L);
  }

  [Test]
  public async Task ReportStreamId_ByTypeWithANonV7Id_NamesTheTypeBySimpleNameAsync() {
    var logger = new CollectingLogger();
    var validator = new GuidOrderingValidator(new WhizbangOptions(), logger);

    validator.ReportStreamId(NonTimeOrderedGuidShapes.V4, streamIdType: typeof(Guid), typeof(StreamIdAllocationProbeNoted), ENTRY_POINT);

    await Assert.That(logger.Entries).Count().IsEqualTo(1);
    await Assert.That(logger.Entries[0].Message).Contains(nameof(StreamIdAllocationProbeNoted)).And.Contains(ENTRY_POINT);
  }

  [Test]
  [Arguments(GuidOrderingSeverity.Error)]
  [Arguments(GuidOrderingSeverity.Warning)]
  public async Task ReportStreamId_ByTypeWithAnEmptyId_IsNeverReportedAsync(GuidOrderingSeverity streamSeverity) {
    var logger = new CollectingLogger();
    var validator = new GuidOrderingValidator(new WhizbangOptions { StreamIdOrderingViolationSeverity = streamSeverity }, logger);

    validator.ReportStreamId(Guid.Empty, streamIdType: null, typeof(StreamIdAllocationProbeNoted), ENTRY_POINT);

    await Assert.That(logger.Entries).IsEmpty();
  }

  [Test]
  public async Task ReportStreamId_ByTypeWithAnAnyVersionIdType_IsNeverReportedAsync() {
    WhizbangIdProviderRegistry.RegisterAcceptsAnyVersion<Whizbang.Core.Tests.ValueObjects.AnyVersionTestId>();
    var logger = new CollectingLogger();
    var validator = new GuidOrderingValidator(
      new WhizbangOptions { StreamIdOrderingViolationSeverity = GuidOrderingSeverity.Error }, logger);

    validator.ReportStreamId(NonTimeOrderedGuidShapes.V4, typeof(Whizbang.Core.Tests.ValueObjects.AnyVersionTestId),
      typeof(StreamIdAllocationProbeNoted), ENTRY_POINT);

    await Assert.That(logger.Entries).IsEmpty();
  }

  [Test]
  public async Task ReportStreamId_ByANullType_ThrowsAsync() {
    var validator = new GuidOrderingValidator(new WhizbangOptions(), new CollectingLogger());

    await Assert.That(() => validator.ReportStreamId(TrackedGuid.New(), streamIdType: null, (Type)null!, ENTRY_POINT))
      .Throws<ArgumentNullException>();
  }

  private sealed class CollectingLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
  }
}

/// <summary>An event for the stream id allocation measurements (#2548).</summary>
/// <param name="StreamId">The stream.</param>
public sealed record StreamIdAllocationProbeNoted([property: StreamId] Guid StreamId) : IEvent;
