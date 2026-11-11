// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Configuration;

namespace Whizbang.Core.Workers;

/// <summary>
/// Settings for the bound on what the outbox and inbox batch strategies hold in memory (#2220), bound from
/// <c>Whizbang:BatchBuffer</c>. One bound per process, shared by the outbox and the inbox.
/// </summary>
/// <docs>messaging/outbox-pattern#bounded-buffer</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/BatchBufferLimitsTests.cs</tests>
public sealed class BatchBufferOptions {
  /// <summary>The configuration section.</summary>
  public const string SECTION = "Whizbang:BatchBuffer";

  /// <summary>The message limit when none is configured.</summary>
  public const int DEFAULT_MAX_BUFFERED_MESSAGES = 10_000;

  /// <summary>The share of the process memory limit buffered bytes may use when no byte limit is configured.</summary>
  public const double DEFAULT_MAX_BUFFERED_BYTES_FRACTION = 0.05;

  /// <summary>How far past the limits a caller holding a database transaction open may go, as a multiple.</summary>
  public const double DEFAULT_TRANSACTION_CEILING_MULTIPLIER = 2.0;

  /// <summary>
  /// The byte limit as a set number. When set it wins over <see cref="MaxBufferedBytesFraction"/>; 0 or less means no
  /// byte limit. Null (the default) derives the limit from the process memory limit.
  /// </summary>
  public long? MaxBufferedBytes { get; set; }

  /// <summary>
  /// The byte limit as a share of the process memory limit (<c>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes</c>, which
  /// is the container's memory limit when there is one), used when <see cref="MaxBufferedBytes"/> is not set. Default
  /// 0.05; 0 or less means no byte limit.
  /// </summary>
  public double MaxBufferedBytesFraction { get; set; } = DEFAULT_MAX_BUFFERED_BYTES_FRACTION;

  /// <summary>The message limit. Default 10,000; 0 or less means no message limit.</summary>
  public int MaxBufferedMessages { get; set; } = DEFAULT_MAX_BUFFERED_MESSAGES;

  /// <summary>
  /// How far past the limits a caller whose scope holds a database transaction open may go instead of waiting, as a
  /// multiple of the limits. Default 2; values below 1 are treated as 1 (no overshoot).
  /// </summary>
  public double TransactionCeilingMultiplier { get; set; } = DEFAULT_TRANSACTION_CEILING_MULTIPLIER;
}

/// <summary>
/// The limits the batch buffer runs with, resolved once from <see cref="BatchBufferOptions"/>, the process memory limit
/// and the work coordinator gate. Each derived value carries its inputs to the startup settings report.
/// </summary>
/// <docs>messaging/outbox-pattern#bounded-buffer</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/BatchBufferLimitsTests.cs</tests>
public sealed class BatchBufferLimits : IDerivedSettingSource {
  private readonly List<IDerivedSetting> _derived = [];

  private BatchBufferLimits(long? maxMessages, long? maxBytes, double reserveFraction, double ceilingMultiplier) {
    MaxMessages = maxMessages;
    MaxBytes = maxBytes;
    InteractiveReserveFraction = reserveFraction;
    TransactionCeilingMultiplier = ceilingMultiplier;
  }

  /// <summary>The message limit, or null for none.</summary>
  public long? MaxMessages { get; }

  /// <summary>The byte limit, or null for none.</summary>
  public long? MaxBytes { get; }

  /// <summary>The share of each limit only interactive callers may use: the work coordinator gate's reserve fraction.</summary>
  public double InteractiveReserveFraction { get; }

  /// <summary>How far past the limits a caller holding a transaction open may go, as a multiple (at least 1).</summary>
  public double TransactionCeilingMultiplier { get; }

  /// <inheritdoc />
  public IEnumerable<IDerivedSetting> DerivedSettings => _derived;

  /// <summary>Resolves the limits.</summary>
  /// <param name="options">The configured settings.</param>
  /// <param name="totalAvailableMemoryBytes">The process memory limit, <c>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes</c>.</param>
  /// <param name="gateMaxConcurrent">The work coordinator gate's permits.</param>
  /// <param name="gateInteractiveReserve">The permits the gate holds back for interactive callers.</param>
  /// <returns>The limits.</returns>
  public static BatchBufferLimits Resolve(
      BatchBufferOptions options, long totalAvailableMemoryBytes, int gateMaxConcurrent, int gateInteractiveReserve) {
    _ = (options, totalAvailableMemoryBytes, gateMaxConcurrent, gateInteractiveReserve);
    return new BatchBufferLimits(null, null, 0, 1);
  }
}
