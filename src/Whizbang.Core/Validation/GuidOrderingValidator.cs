// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Configuration;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Validation;

/// <summary>
/// The time-ordering check every event and message id entry point runs (#2528), and the report for stream ids.
/// </summary>
/// <remarks>
/// <para>
/// Version assignment orders a batch by message id, apply order breaks ties on event id, replay falls back to id order,
/// and <c>wh_lifecycle_completions</c> is partitioned by the time in the event id. An event or message id that is not a
/// UUIDv7 is therefore refused where it enters (<see cref="ValidateMessageId"/>) at
/// <see cref="GuidOrderingSeverity.Error"/>, the default. Below that it is logged once and counted, so the count can be
/// driven to zero. The empty id is refused at every severity. A message type carrying
/// <see cref="SuppressGuidOrderingValidationAttribute"/> (registered in <see cref="GuidOrderingSuppressions"/> by the
/// generator) is a deliberate opt-out: its non-v7 ids are accepted at Error and logged and counted as at
/// <see cref="GuidOrderingSeverity.Warning"/>.
/// </para>
/// <para>
/// A stream id is hashed and sorted for a total order, never read for its time, so a non-v7 stream id costs only index
/// locality: <see cref="ReportStreamId"/> logs it once and counts it, and refuses it only when stream ids are raised to
/// <see cref="GuidOrderingSeverity.Error"/>. A strong id declared to accept any version is never reported.
/// </para>
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids#time-ordering-checks</docs>
/// <tests>tests/Whizbang.Core.Tests/Validation/GuidOrderingEntryPointValidationTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Validation/GuidOrderingValidatorTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Validation/GuidOrderingSuppressionTests.cs</tests>
/// <param name="options">Whizbang configuration options.</param>
/// <param name="logger">Logger for reporting violations.</param>
/// <param name="metrics">The counters for what is accepted below <see cref="GuidOrderingSeverity.Error"/>, or null for none.</param>
public partial class GuidOrderingValidator(WhizbangOptions options, ILogger logger, GuidOrderingMetrics? metrics) {
  /// <summary>
  /// How many distinct ids the once-per-id report remembers before it forgets them all and starts again, so a service
  /// fed a stream of non-v7 ids does not grow without bound; an id reported before the reset may be reported again.
  /// </summary>
#pragma warning disable CA1707 // Repo style: public const fields are ALL_CAPS_SNAKE per editorconfig.
  public const int MAX_REMEMBERED_IDS = 100_000;
#pragma warning restore CA1707

  private readonly WhizbangOptions _options = ArgumentGuard.NotNull(options);
  private readonly ILogger _logger = ArgumentGuard.NotNull(logger);
  private readonly GuidOrderingMetrics? _metrics = metrics;
  private readonly ConcurrentDictionary<Guid, byte> _reportedMessageIds = new();
  private readonly ConcurrentDictionary<Guid, byte> _reportedStreamIds = new();

  /// <summary>
  /// Creates a new GuidOrderingValidator.
  /// </summary>
  /// <param name="options">Whizbang configuration options.</param>
  /// <param name="logger">Logger for reporting violations.</param>
  public GuidOrderingValidator(WhizbangOptions options, ILogger logger) : this(options, logger, metrics: null) { }

  /// <summary>
  /// The validator a component uses when none was registered: the default options (event and message ids refused,
  /// stream ids reported), no logger and no counters.
  /// </summary>
  public static GuidOrderingValidator Default { get; } = new(new WhizbangOptions(), NullLogger.Instance);

  /// <summary>
  /// Checks an event or message id where it enters the framework. A UUIDv7 passes at once; the empty id always throws;
  /// any other version throws at <see cref="GuidOrderingSeverity.Error"/> and is otherwise logged and counted once. A
  /// type carrying <see cref="SuppressGuidOrderingValidationAttribute"/> is checked as if the severity were at most
  /// <see cref="GuidOrderingSeverity.Warning"/>: its id is accepted, logged and counted, never silently unless the
  /// severity is <see cref="GuidOrderingSeverity.None"/>.
  /// </summary>
  /// <param name="id">The event or message id.</param>
  /// <param name="messageType">The stored name of the message's type (<c>Namespace.Type, Assembly</c>), for the opt-out
  /// lookup and the report.</param>
  /// <param name="entryPoint">The entry point checking it, for the report.</param>
  /// <exception cref="GuidOrderingException">The id is empty, or not v7 at <see cref="GuidOrderingSeverity.Error"/>.</exception>
  /// <tests>tests/Whizbang.Core.Tests/Validation/GuidOrderingSuppressionTests.cs:ValidateMessageId_ASuppressedTypeByNameAtError_IsAcceptedAndLoggedAsync</tests>
  public void ValidateMessageId(Guid id, string messageType, string entryPoint) {
    if (id.Version != 7) {
      _checkNonV7(id, messageType, entryPoint, GuidOrderingSuppressions.IsSuppressed(messageType));
    }
  }

  /// <summary>
  /// Checks an event or message id whose message type is known, as <see cref="ValidateMessageId(Guid, string, string)"/>
  /// does; the report names the type by its simple name.
  /// </summary>
  /// <param name="id">The event or message id.</param>
  /// <param name="messageType">The message's type, for the opt-out lookup and the report.</param>
  /// <param name="entryPoint">The entry point checking it, for the report.</param>
  /// <exception cref="ArgumentNullException">The type is null.</exception>
  /// <exception cref="GuidOrderingException">The id is empty, or not v7 at <see cref="GuidOrderingSeverity.Error"/>.</exception>
  /// <tests>tests/Whizbang.Core.Tests/Validation/GuidOrderingSuppressionTests.cs:ValidateMessageId_ASuppressedTypeAtError_IsAcceptedLoggedAsAWarningAndCountedOnceAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Validation/GuidOrderingSuppressionTests.cs:ValidateMessageId_ANullType_ThrowsAsync</tests>
  public void ValidateMessageId(Guid id, Type messageType, string entryPoint) {
    ArgumentNullException.ThrowIfNull(messageType);
    if (id.Version != 7) {
      _checkNonV7(id, TypeNameFormatter.SimpleName(messageType), entryPoint, GuidOrderingSuppressions.IsSuppressed(messageType));
    }
  }

  private void _checkNonV7(Guid id, string messageType, string entryPoint, bool suppressed) {
    if (id == Guid.Empty) {
      throw new GuidOrderingException(id, messageType, entryPoint,
        $"{entryPoint} refused the empty message id {id} for {messageType} (version 0). Event and message ids must be UUIDv7 (time-ordered).");
    }
    var severity = _options.GuidOrderingViolationSeverity;
    if (suppressed && severity == GuidOrderingSeverity.Error) {
      // The type opted out deliberately: accepted, but logged and counted as Warning would, so it stays visible
      severity = GuidOrderingSeverity.Warning;
    }
    if (severity == GuidOrderingSeverity.Error) {
      throw new GuidOrderingException(id, messageType, entryPoint,
        $"{entryPoint} refused message id {id} for {messageType}: version {id.Version}, but event and message ids must be UUIDv7 "
        + "(time-ordered). Mint it with TrackedGuid.New() or a [WhizbangId] type; GuidOrderingViolationSeverity can lower this to a warning.");
    }
    if (severity == GuidOrderingSeverity.None || !_firstSighting(_reportedMessageIds, id)) {
      return;
    }
    _logAcceptedMessageId(severity, suppressed, entryPoint, id, messageType);
    _metrics?.NonV7MessageIds.Add(1, _entryPointTag(entryPoint), _versionTag(id));
  }

  private void _logAcceptedMessageId(GuidOrderingSeverity severity, bool suppressed, string entryPoint, Guid id, string messageType) {
    switch (severity, suppressed) {
      case (GuidOrderingSeverity.Warning, true):
        LogSuppressedMessageIdWarning(_logger, entryPoint, id, messageType, id.Version);
        break;
      case (GuidOrderingSeverity.Warning, false):
        LogMessageIdWarning(_logger, entryPoint, id, messageType, id.Version);
        break;
      case (_, true):
        LogSuppressedMessageIdInfo(_logger, entryPoint, id, messageType, id.Version);
        break;
      default:
        LogMessageIdInfo(_logger, entryPoint, id, messageType, id.Version);
        break;
    }
  }

  /// <summary>
  /// Reports a stream id that is not a UUIDv7, once per stream: logged and counted, or refused when stream ids are
  /// raised to <see cref="GuidOrderingSeverity.Error"/>. The empty id (refused elsewhere, by <see cref="StreamIdGuard"/>),
  /// a UUIDv7 and an id of a type declared to accept any version are never reported. Nothing here compares ids.
  /// </summary>
  /// <param name="streamId">The stream id.</param>
  /// <param name="streamIdType">The stream id's declared type (a strong id or <see cref="Guid"/>), or null when unknown.</param>
  /// <param name="messageType">The type of the message carrying it, for the report.</param>
  /// <param name="entryPoint">The entry point reporting it, for the report.</param>
  /// <exception cref="GuidOrderingException">The stream id is not v7 and stream ids are raised to Error.</exception>
  public void ReportStreamId(Guid streamId, Type? streamIdType, string messageType, string entryPoint) {
    if (streamId.Version == 7 || streamId == Guid.Empty || WhizbangIdProviderRegistry.AcceptsAnyVersion(streamIdType)) {
      return;
    }
    var severity = _options.StreamIdOrderingViolationSeverity;
    if (severity == GuidOrderingSeverity.Error) {
      throw new GuidOrderingException(streamId, messageType, entryPoint,
        $"{entryPoint} refused stream id {streamId} for {messageType}: version {streamId.Version}, and stream ids are configured "
        + "to be UUIDv7 (StreamIdOrderingViolationSeverity = Error).");
    }
    if (severity == GuidOrderingSeverity.None || !_firstSighting(_reportedStreamIds, streamId)) {
      return;
    }
    if (severity == GuidOrderingSeverity.Warning) {
      LogStreamIdWarning(_logger, entryPoint, streamId, messageType, streamId.Version);
    } else {
      LogStreamIdInfo(_logger, entryPoint, streamId, messageType, streamId.Version);
    }
    _metrics?.NonV7StreamIds.Add(1, _entryPointTag(entryPoint), _versionTag(streamId));
  }

  /// <summary>
  /// The check an event store append runs before anything is stored: the envelope's message id (the event id) through
  /// <see cref="ValidateMessageId"/> and the stream id through <see cref="ReportStreamId"/>, both named by the payload's
  /// runtime type.
  /// </summary>
  /// <typeparam name="TMessage">The envelope's payload type.</typeparam>
  /// <param name="streamId">The stream appended to.</param>
  /// <param name="envelope">The envelope appended.</param>
  /// <param name="entryPoint">The store's entry point, for the report.</param>
  /// <exception cref="GuidOrderingException">The event id is refused, or the stream id is and stream ids are raised to Error.</exception>
  public void ValidateAppend<TMessage>(Guid streamId, Observability.MessageEnvelope<TMessage> envelope, string entryPoint) {
    ArgumentNullException.ThrowIfNull(envelope);
    var messageType = envelope.Payload is { } payload ? payload.GetType() : typeof(TMessage);
    ValidateMessageId(envelope.MessageId.Value, messageType, entryPoint);
    ReportStreamId(streamId, streamIdType: null, TypeNameFormatter.SimpleName(messageType), entryPoint);
  }

  /// <summary>
  /// Validates that a TrackedGuid is appropriate for time-sensitive ordering.
  /// </summary>
  /// <param name="trackedId">The TrackedGuid to validate.</param>
  /// <param name="context">Context description (e.g., "EventId", "AggregateId").</param>
  /// <exception cref="GuidOrderingException">
  /// Thrown when severity is Error and the GUID is not time-ordered.
  /// </exception>
  public void ValidateForTimeOrdering(TrackedGuid trackedId, string context) {
    // Skip validation if tracking is disabled
    if (_options.DisableGuidTracking) {
      return;
    }

    // Skip if severity is None
    if (_options.GuidOrderingViolationSeverity == GuidOrderingSeverity.None) {
      return;
    }

    // Check if GUID is time-ordered (v7)
    if (trackedId.IsTimeOrdered) {
      return;
    }

    // Take action based on severity
    switch (_options.GuidOrderingViolationSeverity) {
      case GuidOrderingSeverity.Error:
        LogGuidOrderingError(_logger, context, trackedId.Metadata, trackedId.IsTracking);
        throw new GuidOrderingException(
            $"Non-time-ordered GUID used for {context}. Metadata: {trackedId.Metadata}, IsTracking: {trackedId.IsTracking}");

      case GuidOrderingSeverity.Warning:
        LogGuidOrderingWarning(_logger, context, trackedId.Metadata, trackedId.IsTracking);
        break;

      case GuidOrderingSeverity.Info:
        LogGuidOrderingInfo(_logger, context, trackedId.Metadata, trackedId.IsTracking);
        break;
    }
  }

  // True the first time an id is seen; the memory is cleared when it fills, so it stays bounded.
  private static bool _firstSighting(ConcurrentDictionary<Guid, byte> seen, Guid id) {
    if (seen.Count >= MAX_REMEMBERED_IDS) {
      seen.Clear();
    }
    return seen.TryAdd(id, 0);
  }

  private static KeyValuePair<string, object?> _entryPointTag(string entryPoint) =>
    new(GuidOrderingMetrics.ENTRY_POINT_TAG, entryPoint);

  private static KeyValuePair<string, object?> _versionTag(Guid id) =>
    new(GuidOrderingMetrics.VERSION_TAG, id.Version.ToString(CultureInfo.InvariantCulture));

  [LoggerMessage(
      Level = LogLevel.Warning,
      Message = "{EntryPoint} accepted message id {MessageId} for {MessageType}: version {Version}, not UUIDv7. Version assignment, apply order and replay read the time in event and message ids; mint them with TrackedGuid.New() or a [WhizbangId] type.")]
  private static partial void LogMessageIdWarning(ILogger logger, string entryPoint, Guid messageId, string messageType, int version);

  [LoggerMessage(
      Level = LogLevel.Information,
      Message = "{EntryPoint} accepted message id {MessageId} for {MessageType}: version {Version}, not UUIDv7. Version assignment, apply order and replay read the time in event and message ids; mint them with TrackedGuid.New() or a [WhizbangId] type.")]
  private static partial void LogMessageIdInfo(ILogger logger, string entryPoint, Guid messageId, string messageType, int version);

  [LoggerMessage(
      Level = LogLevel.Warning,
      Message = "{EntryPoint} accepted message id {MessageId} for {MessageType}: version {Version}, not UUIDv7, because the type carries [SuppressGuidOrderingValidation]. Version assignment, apply order and replay read the time in event and message ids, so this type's ids are ordered by their bytes, not by when they were minted.")]
  private static partial void LogSuppressedMessageIdWarning(ILogger logger, string entryPoint, Guid messageId, string messageType, int version);

  [LoggerMessage(
      Level = LogLevel.Information,
      Message = "{EntryPoint} accepted message id {MessageId} for {MessageType}: version {Version}, not UUIDv7, because the type carries [SuppressGuidOrderingValidation]. Version assignment, apply order and replay read the time in event and message ids, so this type's ids are ordered by their bytes, not by when they were minted.")]
  private static partial void LogSuppressedMessageIdInfo(ILogger logger, string entryPoint, Guid messageId, string messageType, int version);

  [LoggerMessage(
      Level = LogLevel.Warning,
      Message = "{EntryPoint} saw stream id {StreamId} for {MessageType}: version {Version}, not UUIDv7. A stream id is never read for its time; a non-v7 one costs only index locality.")]
  private static partial void LogStreamIdWarning(ILogger logger, string entryPoint, Guid streamId, string messageType, int version);

  [LoggerMessage(
      Level = LogLevel.Information,
      Message = "{EntryPoint} saw stream id {StreamId} for {MessageType}: version {Version}, not UUIDv7. A stream id is never read for its time; a non-v7 one costs only index locality.")]
  private static partial void LogStreamIdInfo(ILogger logger, string entryPoint, Guid streamId, string messageType, int version);

  [LoggerMessage(
      Level = LogLevel.Error,
      Message = "Non-time-ordered GUID used for {Context}. Metadata: {Metadata}, IsTracking: {IsTracking}")]
  private static partial void LogGuidOrderingError(ILogger logger, string context, GuidMetadatas metadata, bool isTracking);

  [LoggerMessage(
      Level = LogLevel.Warning,
      Message = "Non-time-ordered GUID used for {Context}. Metadata: {Metadata}, IsTracking: {IsTracking}")]
  private static partial void LogGuidOrderingWarning(ILogger logger, string context, GuidMetadatas metadata, bool isTracking);

  [LoggerMessage(
      Level = LogLevel.Information,
      Message = "Non-time-ordered GUID used for {Context}. Metadata: {Metadata}, IsTracking: {IsTracking}")]
  private static partial void LogGuidOrderingInfo(ILogger logger, string context, GuidMetadatas metadata, bool isTracking);
}
