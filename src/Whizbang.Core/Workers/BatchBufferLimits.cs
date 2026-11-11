// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
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
  private const string MAX_BYTES_KEY = BatchBufferOptions.SECTION + ":MaxBufferedBytes";
  private const string RESERVE_NAME = BatchBufferOptions.SECTION + ":InteractiveReserveFraction";
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
    ArgumentNullException.ThrowIfNull(options);
    DerivedSetting<long>? derivedBytes = null;
    long? maxBytes;
    if (options.MaxBufferedBytes is { } set) {
      maxBytes = set > 0 ? set : null;
    } else if (options.MaxBufferedBytesFraction > 0) {
      derivedBytes = DerivedSetting.FractionOf(MAX_BYTES_KEY, options.MaxBufferedBytesFraction, "TotalAvailableMemoryBytes", totalAvailableMemoryBytes);
      maxBytes = Math.Max(1, derivedBytes.Value);
    } else {
      maxBytes = null;
    }
    var reserve = gateMaxConcurrent > 0 ? (double)gateInteractiveReserve / gateMaxConcurrent : 0;
    var limits = new BatchBufferLimits(
      options.MaxBufferedMessages > 0 ? options.MaxBufferedMessages : null,
      maxBytes,
      reserve,
      Math.Max(1, options.TransactionCeilingMultiplier));
    if (derivedBytes is not null) {
      limits._derived.Add(derivedBytes);
    }
    limits._derived.Add(DerivedSetting.Of(RESERVE_NAME, reserve, "WorkCoordinatorGate InteractiveReserve / MaxConcurrent",
      SettingInput.Of("InteractiveReserve", gateInteractiveReserve), SettingInput.Of("MaxConcurrent", gateMaxConcurrent)));
    return limits;
  }
}

/// <summary>
/// Binds <see cref="BatchBufferOptions"/> from <c>Whizbang:BatchBuffer</c>. The section is named literally at the call so
/// the configuration binder source generator compiles the bind to typed assignments. A host without configuration keeps
/// the code values.
/// </summary>
/// <docs>messaging/outbox-pattern#bounded-buffer</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/OutboxBatchStrategyRegistrationTests.cs</tests>
internal sealed class ConfigureBatchBufferOptions(IConfiguration? configuration) : IConfigureOptions<BatchBufferOptions> {
  public void Configure(BatchBufferOptions options) {
    if (configuration is null) {
      return;
    }
#pragma warning disable IL2026, IL3050 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
    configuration.GetSection("Whizbang:BatchBuffer").Bind(options);
#pragma warning restore IL2026, IL3050
  }
}
