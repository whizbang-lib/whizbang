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

  /// <summary>
  /// A stream id of a type declared with a <c>Versions</c> list is not reported for a version the list holds, at Error
  /// either (#2516): the id's own rule accepts it, so the only cost is index locality.
  /// </summary>
  [Test]
  [Arguments(GuidOrderingSeverity.Error)]
  [Arguments(GuidOrderingSeverity.Warning)]
  public async Task ReportStreamId_AVersionTheIdTypeLists_IsNeverReportedAsync(GuidOrderingSeverity streamSeverity) {
    var logger = new CollectingLogger();
    var validator = new GuidOrderingValidator(new WhizbangOptions { StreamIdOrderingViolationSeverity = streamSeverity }, logger);

    validator.ReportStreamId(NonTimeOrderedGuidShapes.V5, typeof(Whizbang.Core.Tests.ValueObjects.MintedOrNamedTestId),
      typeof(StreamIdAllocationProbeNoted), ENTRY_POINT);

    await Assert.That(logger.Entries).IsEmpty().Because("MintedOrNamedTestId lists UUIDv5, registered by its generated module initializer");
  }

  /// <summary>A version the type's <c>Versions</c> list does not hold is still reported, and refused at Error (#2516).</summary>
  [Test]
  public async Task ReportStreamId_AVersionTheIdTypeDoesNotList_IsRefusedAtErrorAsync() {
    var validator = new GuidOrderingValidator(
      new WhizbangOptions { StreamIdOrderingViolationSeverity = GuidOrderingSeverity.Error }, new CollectingLogger());

    await Assert.That(() => validator.ReportStreamId(NonTimeOrderedGuidShapes.V4,
        typeof(Whizbang.Core.Tests.ValueObjects.MintedOrNamedTestId), typeof(StreamIdAllocationProbeNoted), ENTRY_POINT))
      .Throws<GuidOrderingException>();
  }

  /// <summary>
  /// A value the type grandfathers is accepted by its own rule, so it is not reported either (#2517); another value of
  /// the same version is.
  /// </summary>
  [Test]
  public async Task ReportStreamId_AGrandfatheredValue_IsNeverReported_AndAnotherOfItsVersionIsAsync() {
    var logger = new CollectingLogger();
    var validator = new GuidOrderingValidator(
      new WhizbangOptions { StreamIdOrderingViolationSeverity = GuidOrderingSeverity.Warning }, logger);
    var type = typeof(Whizbang.Core.Tests.ValueObjects.GrandfatheredTestId);

    validator.ReportStreamId(Whizbang.Core.Tests.ValueObjects.GrandfatheredTestId.SeededV4, type, typeof(StreamIdAllocationProbeNoted), ENTRY_POINT);
    await Assert.That(logger.Entries).IsEmpty();

    validator.ReportStreamId(NonTimeOrderedGuidShapes.V4, type, typeof(StreamIdAllocationProbeNoted), ENTRY_POINT);
    await Assert.That(logger.Entries).Count().IsEqualTo(1);
  }

  /// <summary>A listed version with a variant other than RFC 9562's is not accepted by the list (#2516).</summary>
  [Test]
  public async Task AcceptsStreamId_AListedVersionWithAnotherVariant_IsNotAcceptedAsync() {
    var nonRfcV5 = Guid.Parse("886313e1-3b8a-5372-1b90-0c9aee199e5d");

    await Assert.That(WhizbangIdProviderRegistry.AcceptsStreamId(typeof(Whizbang.Core.Tests.ValueObjects.MintedOrNamedTestId), nonRfcV5)).IsFalse();
    await Assert.That(WhizbangIdProviderRegistry.AcceptsStreamId(typeof(Whizbang.Core.Tests.ValueObjects.MintedOrNamedTestId), NonTimeOrderedGuidShapes.V5)).IsTrue();
    await Assert.That(WhizbangIdProviderRegistry.AcceptsStreamId(null, NonTimeOrderedGuidShapes.V5)).IsFalse();
    await Assert.That(WhizbangIdProviderRegistry.AcceptsStreamId(typeof(Guid), NonTimeOrderedGuidShapes.V5)).IsFalse();
  }

  /// <summary>
  /// The report asks the id's own IsAccepted, which never throws, rather than calling From and catching what it throws:
  /// a grandfathered value, a listed version and a v7 one are accepted; a refused value is answered false (#2516, #2517).
  /// </summary>
  [Test]
  public async Task AcceptsStreamId_AsksTheIdsOwnRule_WithoutAnExceptionAsync() {
    var grandfathered = typeof(Whizbang.Core.Tests.ValueObjects.GrandfatheredTestId);

    await Assert.That(WhizbangIdProviderRegistry.AcceptsStreamId(grandfathered, Whizbang.Core.Tests.ValueObjects.GrandfatheredTestId.SeededV4)).IsTrue();
    await Assert.That(WhizbangIdProviderRegistry.AcceptsStreamId(grandfathered, NonTimeOrderedGuidShapes.V5)).IsFalse();
    await Assert.That(Whizbang.Core.Tests.ValueObjects.GrandfatheredTestId.IsAccepted(Whizbang.Core.Tests.ValueObjects.GrandfatheredTestId.SeededV8)).IsTrue();
    await Assert.That(Whizbang.Core.Tests.ValueObjects.GrandfatheredTestId.IsAccepted(Guid.Empty)).IsFalse()
      .Because("the empty Guid is never grandfathered, whatever the hook says");
    await Assert.That(Whizbang.Core.Tests.ValueObjects.MintedOrNamedTestId.IsAccepted(NonTimeOrderedGuidShapes.V5)).IsTrue();
    await Assert.That(Whizbang.Core.Tests.ValueObjects.StrictVersionTestId.IsAccepted(NonTimeOrderedGuidShapes.V5)).IsFalse();
    await Assert.That(Whizbang.Core.Tests.ValueObjects.AnyVersionTestId.IsAccepted(Guid.Empty)).IsFalse();
  }

  /// <summary>
  /// A grandfathered-values hook that throws is the id type's bug, not the append's: the stream id is answered as not
  /// accepted, so it is reported, and nothing escapes to fail the append.
  /// </summary>
  [Test]
  public async Task AcceptsStreamId_AHookThatThrows_ReportsTheStreamIdRatherThanFailingTheAppendAsync() {
    var id = typeof(Whizbang.Core.Tests.ValueObjects.ThrowingHookTestId);
    var logger = new CollectingLogger();
    var validator = new GuidOrderingValidator(new WhizbangOptions(), logger);

    var accepted = WhizbangIdProviderRegistry.AcceptsStreamId(id, Whizbang.Core.Tests.ValueObjects.ThrowingHookTestId.Throws);
    validator.ReportStreamId(Whizbang.Core.Tests.ValueObjects.ThrowingHookTestId.Throws, id, typeof(StreamIdAllocationProbeNoted), ENTRY_POINT);

    await Assert.That(accepted).IsFalse();
    await Assert.That(logger.Entries).IsNotEmpty().Because("the stream id is reported");
  }

  /// <summary>
  /// A hook that throws is logged at Warning, once per stream id, naming the id type, the stream id and what it threw,
  /// and the out overload hands back what it threw.
  /// </summary>
  [Test]
  public async Task AcceptsStreamId_AHookThatThrows_IsLoggedAtWarningNamingTheIdTypeAndStreamIdAsync() {
    var id = typeof(Whizbang.Core.Tests.ValueObjects.ThrowingHookTestId);
    var value = Whizbang.Core.Tests.ValueObjects.ThrowingHookTestId.Throws;
    var logger = new CollectingLogger();
    var validator = new GuidOrderingValidator(new WhizbangOptions(), logger);

    var accepted = WhizbangIdProviderRegistry.AcceptsStreamId(id, value, out var failure);
    validator.ReportStreamId(value, id, typeof(StreamIdAllocationProbeNoted), ENTRY_POINT);
    validator.ReportStreamId(value, id, typeof(StreamIdAllocationProbeNoted), ENTRY_POINT);

    var hookLogs = logger.Entries.Where(e => e.Message.Contains("IsAccepted threw", StringComparison.Ordinal)).ToList();
    await Assert.That(accepted).IsFalse();
    await Assert.That(failure).IsNotNull();
    await Assert.That(hookLogs.Count).IsEqualTo(1).Because("logged once per stream id");
    await Assert.That(hookLogs[0].Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(hookLogs[0].Message).Contains($"ThrowingHookTestId.IsAccepted threw for stream id {value}");
    await Assert.That(hookLogs[0].Exception).IsNotNull();
  }

  [Test]
  public async Task ReportStreamId_ByANullType_ThrowsAsync() {
    var validator = new GuidOrderingValidator(new WhizbangOptions(), new CollectingLogger());

    await Assert.That(() => validator.ReportStreamId(TrackedGuid.New(), streamIdType: null, (Type)null!, ENTRY_POINT))
      .Throws<ArgumentNullException>();
  }

  private sealed class CollectingLogger : ILogger {
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception), exception));
  }
}

/// <summary>An event for the stream id allocation measurements (#2548).</summary>
/// <param name="StreamId">The stream.</param>
public sealed record StreamIdAllocationProbeNoted([property: StreamId] Guid StreamId) : IEvent;
