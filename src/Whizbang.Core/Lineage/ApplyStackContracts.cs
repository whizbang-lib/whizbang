namespace Whizbang.Core.Lineage;

/// <summary>
/// One distinct apply-path shape through a perspective's event view, with the number of streams
/// that took it. The <paramref name="Path"/> is the ordered event-type sequence a stream's events
/// were applied in (version order), run-length collapsed: a run of two or more consecutive
/// identical types appears once with a <c>+</c> suffix (<c>StatusUpdated+</c>), a single
/// occurrence appears plain. Aggregate size therefore scales with distinct shapes, not with
/// streams or events.
/// </summary>
/// <param name="Path">The run-length-collapsed, version-ordered event-type sequence.</param>
/// <param name="StreamCount">How many streams share this exact collapsed shape.</param>
/// <param name="FirstSeen">The earliest head-event time among streams with this shape.</param>
/// <param name="LastSeen">The latest head-event time among streams with this shape.</param>
/// <docs>proposals/pre-destruction-seam#serving-the-view</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ApplyStackQuerySqlTests.cs</tests>
public sealed record ApplyPathSignature(
  IReadOnlyList<string> Path,
  long StreamCount,
  DateTimeOffset FirstSeen,
  DateTimeOffset LastSeen);

/// <summary>
/// Filters for the apply-stack query. Every filter narrows the event rows that form the
/// per-stream paths; a stream none of whose events survive the filters contributes no path.
/// </summary>
/// <docs>proposals/pre-destruction-seam#serving-the-view</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ApplyStackQuerySqlTests.cs</tests>
public sealed record ApplyStackQueryOptions {
  /// <summary>
  /// Restrict paths to the event types associated with this perspective (the association
  /// registry's <c>perspective</c> rows). Null = all event types, the whole-store view.
  /// </summary>
  public string? PerspectiveName { get; init; }

  /// <summary>
  /// JSONB containment filter applied to each event's scope (<c>scope @&gt; value</c>), so a
  /// tenant-scoped caller sees only its own shapes. Null = no scope filter.
  /// </summary>
  public string? ScopeJson { get; init; }

  /// <summary>Maximum number of signatures returned, heaviest first. Default 200.</summary>
  public int MaxSignatures { get; init; } = 200;
}

/// <summary>
/// The apply-stack query surface: on-demand aggregation of the ordered event paths that built
/// each stream, grouped into <see cref="ApplyPathSignature"/> counts. Read-only and analytical —
/// it reads event-store pointers (never bodies) and runs nothing on the hot path. Supplied by the
/// data driver; every serving surface (minimal API, FastEndpoints, HotChocolate, the VS Code
/// extension's direct-DB mode) consumes this one contract so the transports cannot drift in what
/// they disclose.
/// </summary>
/// <docs>proposals/pre-destruction-seam#serving-the-view</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ApplyStackQuerySqlTests.cs</tests>
public interface IApplyStackQuery {
  /// <summary>
  /// Aggregates the distinct apply-path shapes matching <paramref name="options"/>, heaviest
  /// (most streams) first.
  /// </summary>
  /// <param name="options">Filters narrowing the event rows that form the paths.</param>
  /// <param name="cancellationToken">Cancels the query.</param>
  Task<IReadOnlyList<ApplyPathSignature>> GetPathSignaturesAsync(
    ApplyStackQueryOptions options,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Drill-in: the stream ids whose collapsed path equals <paramref name="path"/> exactly, under
  /// the same filters used to compute signatures.
  /// </summary>
  /// <param name="path">The collapsed path, exactly as an <see cref="ApplyPathSignature"/> returned it.</param>
  /// <param name="options">The same filters the signature listing used.</param>
  /// <param name="limit">Maximum stream ids returned.</param>
  /// <param name="cancellationToken">Cancels the query.</param>
  Task<IReadOnlyList<Guid>> GetStreamsForPathAsync(
    IReadOnlyList<string> path,
    ApplyStackQueryOptions options,
    int limit,
    CancellationToken cancellationToken = default);
}
