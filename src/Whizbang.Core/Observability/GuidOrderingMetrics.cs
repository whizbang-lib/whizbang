// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// Counts the ids that entered the framework without the time order they need (#2528): event and message ids that are
/// not UUIDv7 and were accepted because the severity is below <see cref="Configuration.GuidOrderingSeverity.Error"/>,
/// and stream ids that are not UUIDv7. Each id is counted once, so the totals are distinct ids and can be driven to zero.
/// </summary>
/// <docs>operations/observability/metrics#time-ordering-checks</docs>
/// <tests>tests/Whizbang.Core.Tests/Validation/GuidOrderingEntryPointValidationTests.cs</tests>
public sealed class GuidOrderingMetrics {
#pragma warning disable CA1707 // Repo style: public const fields are ALL_CAPS_SNAKE per editorconfig.
  /// <summary>Meter name, listed in <see cref="WhizbangMeters"/>.</summary>
  public const string METER_NAME = "Whizbang.GuidOrdering";

  /// <summary>Distinct non-v7 event and message ids accepted at an entry point.</summary>
  public const string NON_V7_MESSAGE_IDS = "whizbang.guid_ordering.non_v7_message_ids";

  /// <summary>Distinct non-v7 stream ids seen at an entry point.</summary>
  public const string NON_V7_STREAM_IDS = "whizbang.guid_ordering.non_v7_stream_ids";

  /// <summary>The tag naming the entry point.</summary>
  public const string ENTRY_POINT_TAG = "entry_point";

  /// <summary>The tag carrying the id's version.</summary>
  public const string VERSION_TAG = "version";

  /// <summary>
  /// The tag on <see cref="NON_V7_MESSAGE_IDS"/> saying why the id was accepted: the boolean <see langword="true"/> when
  /// its message type carries <see cref="SuppressGuidOrderingValidationAttribute"/>, <see langword="false"/> when the
  /// configured severity alone let it through. Always present on that counter, never omitted; never on
  /// <see cref="NON_V7_STREAM_IDS"/>, which the attribute does not affect.
  /// </summary>
  public const string SUPPRESSED_TAG = "suppressed";
#pragma warning restore CA1707

  /// <summary>Initializes the meters on the shared Whizbang meter factory.</summary>
  /// <param name="whizbangMetrics">The shared meter factory holder.</param>
  /// <exception cref="ArgumentNullException">Thrown when the holder is null.</exception>
  public GuidOrderingMetrics(WhizbangMetrics whizbangMetrics) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    var meter = whizbangMetrics.MeterFactory.Create(METER_NAME);
    NonV7MessageIds = meter.CreatePassiveCounter<long>(NON_V7_MESSAGE_IDS,
      description: "Distinct event and message ids accepted although they are not UUIDv7; tagged by entry_point and version");
    NonV7StreamIds = meter.CreatePassiveCounter<long>(NON_V7_STREAM_IDS,
      description: "Distinct stream ids that are not UUIDv7 (index locality only); tagged by entry_point and version");
  }

  /// <summary>Distinct non-v7 event and message ids accepted.</summary>
  public PassiveCounter<long> NonV7MessageIds { get; }

  /// <summary>Distinct non-v7 stream ids seen.</summary>
  public PassiveCounter<long> NonV7StreamIds { get; }
}
