using Microsoft.Extensions.Logging;
using Whizbang.Core.Configuration;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Validation;

/// <summary>
/// Validates that TrackedGuid values are appropriate for time-sensitive ordering.
/// Logs/warns/errors based on configuration when v4 or unknown source is used.
/// </summary>
/// <remarks>
/// Creates a new GuidOrderingValidator.
/// </remarks>
/// <param name="options">Whizbang configuration options.</param>
/// <param name="logger">Logger for reporting violations.</param>
public partial class GuidOrderingValidator(WhizbangOptions options, ILogger logger) {
  private readonly WhizbangOptions _options = options ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
