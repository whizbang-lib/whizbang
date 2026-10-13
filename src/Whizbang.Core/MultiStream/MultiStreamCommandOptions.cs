// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.MultiStream;

/// <summary>How each instance works its share of multi-stream command runs. Bound from <c>Whizbang:MultiStreamCommands</c>.</summary>
/// <docs>fundamentals/dispatcher/multi-stream-commands#configuration</docs>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/MultiStreamCommandWorkerTests.cs:Options_Validate_RefusesANonPositiveSettingAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/MultiStreamCommandWorkerTests.cs:Options_Validate_AcceptsTheDefaultsAsync</tests>
public sealed class MultiStreamCommandOptions {
  /// <summary>The configuration section the options bind from.</summary>
  public const string SECTION = "Whizbang:MultiStreamCommands";

  /// <summary>False stops this instance working runs; runs it owns wait until an instance that works them owns them. Default true.</summary>
  public bool Enabled { get; set; } = true;

  /// <summary>The most streams claimed in one pass. Default 50.</summary>
  public int BatchSize { get; set; } = 50;

  /// <summary>How long a claimed stream is held before it is offered again, in case this instance stops. Default 5 minutes.</summary>
  public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(5);

  /// <summary>Attempts a stream gets before a failure is final. Default 3.</summary>
  public int MaxAttempts { get; set; } = 3;

  /// <summary>How long the worker waits for a new-run notice before looking anyway. Default 30 seconds.</summary>
  public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>How long a completed run is kept, for its status to be read. Default 7 days.</summary>
  public TimeSpan CompletedRunRetention { get; set; } = TimeSpan.FromDays(7);

  /// <summary>How often completed runs past their retention are deleted. Default 1 hour.</summary>
  public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromHours(1);

  /// <summary>Throws when a setting would stop the worker making progress.</summary>
  /// <exception cref="InvalidOperationException">A count or interval is zero or negative.</exception>
  public void Validate() {
    _positive(BatchSize > 0, nameof(BatchSize));
    _positive(Lease > TimeSpan.Zero, nameof(Lease));
    _positive(MaxAttempts > 0, nameof(MaxAttempts));
    _positive(PollInterval > TimeSpan.Zero, nameof(PollInterval));
    _positive(CompletedRunRetention > TimeSpan.Zero, nameof(CompletedRunRetention));
    _positive(PurgeInterval > TimeSpan.Zero, nameof(PurgeInterval));
  }

  private static void _positive(bool holds, string name) {
    if (!holds) {
      throw new InvalidOperationException($"{SECTION}:{name} must be greater than zero.");
    }
  }
}
