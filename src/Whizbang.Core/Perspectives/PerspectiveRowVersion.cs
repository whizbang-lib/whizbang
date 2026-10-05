// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Whizbang.Core.Lenses;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// What a store knew about a perspective row's version when a per-stream apply read it.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives#concurrent-writers</docs>
public enum PerspectiveRowVersionState {
  /// <summary>
  /// The store does not track row versions, so the write is not checked. The default, which keeps every
  /// store written before row versions existed working exactly as it did.
  /// </summary>
  Unchecked = 0,

  /// <summary>There was no row. The write must create it, and is refused if a row appeared meanwhile.</summary>
  Absent = 1,

  /// <summary>There was a row at <see cref="PerspectiveRowVersion.Value"/>. The write lands only on that version.</summary>
  Present = 2,
}

/// <summary>
/// The version of a perspective row as a per-stream apply read it, carried to that apply's write so a
/// write computed from a stale read cannot land. A store that tracks versions refuses a write whose row
/// moved since it was read with <see cref="PerspectiveRowConflictException"/>, and the generated runner
/// re-reads, re-applies and writes again.
/// </summary>
/// <remarks>
/// <para>
/// The value is opaque and store-defined; compare versions for equality only. The PostgreSQL stores use the
/// row's <c>xmin</c>, which every <c>UPDATE</c> moves whichever path issued it (a per-stream write, a
/// collective apply, a consumer's own SQL) and whether or not it bumped the <c>version</c> column, so
/// no writer has to cooperate for the check to see it.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives#concurrent-writers</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowVersionTests.cs</tests>
public readonly record struct PerspectiveRowVersion {
  private PerspectiveRowVersion(PerspectiveRowVersionState state, long value) {
    State = state;
    Value = value;
  }

  /// <summary>What the store knew about the row.</summary>
  public PerspectiveRowVersionState State { get; }

  /// <summary>The row's version when <see cref="State"/> is <see cref="PerspectiveRowVersionState.Present"/>; otherwise zero.</summary>
  public long Value { get; }

  /// <summary>True when the write is to be checked against this version.</summary>
  public bool IsChecked => State != PerspectiveRowVersionState.Unchecked;

  /// <summary>The store does not track versions; the write is not checked.</summary>
  public static PerspectiveRowVersion Unchecked => default;

  /// <summary>There was no row when it was read.</summary>
  public static PerspectiveRowVersion Absent { get; } = new(PerspectiveRowVersionState.Absent, 0);

  /// <summary>There was a row at <paramref name="value"/> when it was read.</summary>
  /// <param name="value">The store-defined version the row carried.</param>
  public static PerspectiveRowVersion Of(long value) => new(PerspectiveRowVersionState.Present, value);

  /// <inheritdoc />
  public override string ToString() => State switch {
    PerspectiveRowVersionState.Unchecked => "unchecked",
    PerspectiveRowVersionState.Absent => "absent",
    _ => Value.ToString(CultureInfo.InvariantCulture),
  };
}

/// <summary>
/// What a per-stream apply learns from its first read of a row: the row's version, and the metadata the
/// runner's idempotency filter needs (the last applied event's id, type and commit sequence). Read in one
/// statement, before the model, so the model the apply folds onto can never be newer than the version its
/// write is checked against.
/// </summary>
/// <param name="Version">The row version the write will be checked against.</param>
/// <param name="Metadata">
/// The row's metadata when the store read it with the version (only <see cref="PerspectiveMetadata.EventId"/>,
/// <see cref="PerspectiveMetadata.EventType"/> and <see cref="PerspectiveMetadata.CommitSequence"/> are
/// populated); null when the row is absent or the store did not read it.
/// </param>
/// <docs>fundamentals/perspectives/perspectives#concurrent-writers</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowVersionTests.cs</tests>
public sealed record PerspectiveApplyRead(PerspectiveRowVersion Version, PerspectiveMetadata? Metadata) {
  /// <summary>The read of a store that does not track versions: nothing is known, so nothing is checked.</summary>
  public static PerspectiveApplyRead Unchecked { get; } = new(PerspectiveRowVersion.Unchecked, null);
}
