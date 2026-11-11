// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace Whizbang.Core.Perspectives;

/// <summary>How a perspective's rows are written when the worker applies a batch of claimed work.</summary>
/// <remarks>
/// Only named modes are selectable, never free combinations of the seams behind them (the state source, the flush
/// policy and the write sink), so a combination that was never tested together cannot be configured.
/// </remarks>
/// <docs>fundamentals/perspectives/write-modes#selection</docs>
public enum PerspectiveWriteMode {
  /// <summary>
  /// The default. Every (stream, perspective) pair of a batch is read, folded and prepared, the batch's rows are
  /// written with a few set-based statements per table, and each pair is then completed. A pair the batch write does
  /// not take (a rewind, a purged or evicted row, a row that moved) takes the per-stream path on its own.
  /// </summary>
  SetBased = 0,

  /// <summary>Each pair is read, folded and written on its own, with its own round trips: the path before set-based writes.</summary>
  PerStream = 1,
}

/// <summary>
/// Overrides the service's perspective write mode for one perspective (<see cref="PerspectiveWriteMode"/>).
/// </summary>
/// <remarks>
/// The generator reads it and registers the perspective's mode, so the worker knows it without resolving the runner.
/// A perspective without it takes the service's mode (<c>PerspectiveWorkerOptions.WriteMode</c>).
/// </remarks>
/// <param name="mode">The perspective's write mode.</param>
/// <docs>fundamentals/perspectives/write-modes#selection</docs>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveWriteModeGeneratorTests.cs</tests>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PerspectiveWriteModeAttribute(PerspectiveWriteMode mode) : Attribute {
  /// <summary>The perspective's write mode.</summary>
  public PerspectiveWriteMode Mode { get; } = mode;
}

/// <summary>
/// The perspectives whose generated runners can write set-based, and the mode each one declares: registered by every
/// generated runner when its assembly loads, read by the worker to choose each pair's path.
/// </summary>
/// <remarks>
/// A perspective that is not registered has no generated runner (a hand-written one, which prepares nothing), so it is
/// always written per stream, whatever the service's mode.
/// </remarks>
/// <docs>fundamentals/perspectives/write-modes#selection</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveWriteModesTests.cs</tests>
public static class PerspectiveWriteModes {
  private static readonly ConcurrentDictionary<string, PerspectiveWriteMode?> _declared = new(StringComparer.Ordinal);

  /// <summary>Registers a generated runner's perspective, with the mode it declares, if any.</summary>
  /// <param name="perspectiveName">The perspective's name, as work items carry it.</param>
  /// <param name="declared">The mode its <see cref="PerspectiveWriteModeAttribute"/> declares, or null.</param>
  public static void Register(string perspectiveName, PerspectiveWriteMode? declared) {
    ArgumentException.ThrowIfNullOrWhiteSpace(perspectiveName);
    _declared[perspectiveName] = declared;
  }

  /// <summary>The perspective's write mode: its declared one, else the service's, and per stream for a perspective with no generated runner.</summary>
  /// <param name="perspectiveName">The perspective's name.</param>
  /// <param name="serviceMode">The service's mode.</param>
  /// <returns>The mode the worker writes the perspective's rows with.</returns>
  public static PerspectiveWriteMode Resolve(string perspectiveName, PerspectiveWriteMode serviceMode) {
    ArgumentNullException.ThrowIfNull(perspectiveName);
    return _declared.TryGetValue(perspectiveName, out var declared)
      ? declared ?? serviceMode
      : PerspectiveWriteMode.PerStream;
  }
}
