// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// A folded model the write-behind mode (#2247) holds in memory instead of writing: the row the next batch of its stream
/// folds onto, as the database will hold it once the held writes are flushed.
/// </summary>
/// <remarks>
/// <para>
/// The model is kept serialized, so the held copy cannot change under a later fold and its size is what the cache's
/// <c>MaxBytes</c> bound measures. Each read materializes a fresh instance.
/// </para>
/// <para>
/// <see cref="ApplyRead"/> carries the version the row has in the database (the version the first held write was
/// prepared on), so every later write is prepared, and the merged write checked, against the database row: a writer
/// outside the runner that moved the row meanwhile (a collective, a rebuild, another instance) turns the flush into a
/// conflict, and each held batch is then re-applied on the row it finds.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/write-modes#write-behind</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveWriteBehindRowTests.cs</tests>
public abstract class PerspectiveWriteBehindRow {
  /// <summary>Creates a held row.</summary>
  /// <param name="applyRead">The row's database version, with the metadata and scope of the last held write.</param>
  /// <param name="lastEventId">The last event folded into the model.</param>
  /// <param name="bytes">The serialized model's size.</param>
  protected PerspectiveWriteBehindRow(PerspectiveApplyRead applyRead, Guid lastEventId, long bytes) {
    ArgumentNullException.ThrowIfNull(applyRead);
    ArgumentOutOfRangeException.ThrowIfNegative(bytes);
    ApplyRead = applyRead;
    LastEventId = lastEventId;
    Bytes = bytes;
  }

  /// <summary>The row's database version, with the metadata and scope of the last held write.</summary>
  public PerspectiveApplyRead ApplyRead { get; }

  /// <summary>The last event folded into the model: the position the stream's next batch is read after.</summary>
  public Guid LastEventId { get; }

  /// <summary>The serialized model's size, in bytes.</summary>
  public long Bytes { get; }

  /// <summary>The row as a batched read returns it, with a fresh model, or null when the model is not a <typeparamref name="TModel"/>.</summary>
  /// <typeparam name="TModel">The perspective's model.</typeparam>
  /// <returns>The row, or null.</returns>
  public abstract PerspectiveStoredRow<TModel>? ReadAs<TModel>() where TModel : class;
}
