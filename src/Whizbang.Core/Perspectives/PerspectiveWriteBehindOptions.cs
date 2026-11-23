// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The bounds of the write-behind model cache (#2247), bound from <c>Whizbang:Workers:Perspective:WriteBehind</c>.
/// </summary>
/// <remarks>
/// <para>
/// Write-behind holds folded models in memory, so it has no unlimited default: <see cref="MaxModels"/> and
/// <see cref="MaxBytes"/> are required whenever any perspective writes behind, and the worker refuses to start without
/// them (<see cref="Validate"/>). A model that does not fit is written at once, as set-based writes it, and the oldest
/// models are flushed whenever the cache is over either bound.
/// </para>
/// <para>
/// A held model's work rows stay leased to this instance until it is flushed, so a model is flushed no later than
/// <see cref="MaxFlushIntervalMilliseconds"/> after it was first held, and that interval must leave the lease room: at
/// most half of the lease less its grace.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/write-modes#write-behind</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveWriteBehindOptionsTests.cs</tests>
public sealed class PerspectiveWriteBehindOptions {
  /// <summary>
  /// The most models held at once, across every write-behind perspective of the worker. Required when any perspective
  /// writes behind; there is no default.
  /// </summary>
  public int? MaxModels { get; set; }

  /// <summary>
  /// The most bytes the held models take, measured as their serialized size. Required when any perspective writes
  /// behind; there is no default.
  /// </summary>
  public long? MaxBytes { get; set; }

  /// <summary>
  /// How long a model is held at most before it is flushed, from the batch that first held it. Default 1,000 ms. It must
  /// be at most half of the perspective lease less its grace, so a held model is flushed while its rows are still leased.
  /// </summary>
  public int MaxFlushIntervalMilliseconds { get; set; } = 1000;

  /// <summary>How many batches one model holds at most before it is flushed. Default 32.</summary>
  public int MaxBatchesPerModel { get; set; } = 32;

  /// <summary>
  /// Checks the bounds of a worker on which <paramref name="writeBehindPerspectives"/> write behind (none when
  /// the list is empty and the service's mode is not <see cref="PerspectiveWriteMode.WriteBehind"/>).
  /// </summary>
  /// <param name="options">The bounds.</param>
  /// <param name="serviceMode">The service's write mode.</param>
  /// <param name="writeBehindPerspectives">The perspectives that declare write-behind.</param>
  /// <param name="leaseSeconds">How long a claimed row is leased.</param>
  /// <param name="leaseGraceSeconds">How long before the lease runs out a holder stops relying on it.</param>
  /// <returns>Null when the bounds are valid or nothing writes behind; otherwise what is wrong and how to fix it.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveWriteBehindOptionsTests.cs</tests>
  public static string? Validate(
      PerspectiveWriteBehindOptions options,
      PerspectiveWriteMode serviceMode,
      IReadOnlyList<string> writeBehindPerspectives,
      int leaseSeconds,
      int leaseGraceSeconds) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(writeBehindPerspectives);
    if (serviceMode != PerspectiveWriteMode.WriteBehind && writeBehindPerspectives.Count == 0) {
      return null;
    }
    var who = serviceMode == PerspectiveWriteMode.WriteBehind
      ? "the service's perspective write mode is WriteBehind"
      : $"perspective(s) {string.Join(", ", writeBehindPerspectives)} declare [PerspectiveWriteMode(WriteBehind)]";
    var problems = new List<string>();
    if (options.MaxModels is not > 0) {
      problems.Add("MaxModels is not set to a positive number");
    }
    if (options.MaxBytes is not > 0) {
      problems.Add("MaxBytes is not set to a positive number");
    }
    if (options.MaxBatchesPerModel < 1) {
      problems.Add("MaxBatchesPerModel is below 1");
    }
    var holdLimitMs = Math.Max(0, leaseSeconds - leaseGraceSeconds) * 1000L / 2;
    if (options.MaxFlushIntervalMilliseconds < 1 || options.MaxFlushIntervalMilliseconds > holdLimitMs) {
      problems.Add(string.Create(CultureInfo.InvariantCulture,
        $"MaxFlushIntervalMilliseconds ({options.MaxFlushIntervalMilliseconds}) is not between 1 and {holdLimitMs}, half of the lease ({leaseSeconds} s) less its grace ({leaseGraceSeconds} s)"));
    }
    if (problems.Count == 0) {
      return null;
    }
    return $"Write-behind perspective writes hold models in memory, so they need bounds: {who}, but "
      + $"{string.Join("; ", problems)}. Set Whizbang:Workers:Perspective:WriteBehind:MaxModels and MaxBytes "
      + "(PerspectiveWorkerOptions.WriteBehind), or choose the SetBased write mode.";
  }
}
