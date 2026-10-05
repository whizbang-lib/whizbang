namespace Whizbang.Core.Perspectives;

/// <summary>
/// Computes a digest of a perspective's rows for a known set of streams, so a rebuild can record what its rows
/// looked like before and after it ran.
/// </summary>
/// <remarks>
/// <para>
/// Cursor movement proves a rebuild EXECUTED. It cannot show whether anything CHANGED, and the two questions have
/// different answers: a rebuild that runs and correctly finds nothing to do is indistinguishable, from the
/// outside, from one that runs and writes back the same wrong data. A digest taken before and after separates
/// them — equal digests mean the rebuild changed nothing, different digests mean it changed something, and no
/// digest at all means it never ran.
/// </para>
/// <para>
/// <strong>Deliberately scoped to a known set of streams.</strong> For a rebuild of selected streams the targeted
/// set is small and known, so digesting those rows costs in proportion to the repair. A whole-perspective rebuild
/// would have to scan and hash the entire table it is about to replace, which is a different cost and wants a
/// different mechanism; implementations are not expected to serve it.
/// </para>
/// <para>
/// <strong>Content, not touch.</strong> An implementation should digest the projected data and exclude the
/// bookkeeping a write always changes, so that "the digest moved" means the row's content moved. Whether the row
/// was written at all is already answered by its cursor.
/// </para>
/// <para>
/// Returning <c>null</c> is the right answer whenever the digest cannot be computed — an unmapped perspective, a
/// driver that cannot do it, a table that is not there. A caller records "not known", which is honest. It must
/// never substitute a digest of nothing, because that compares equal to another digest of nothing and would read
/// as "the rebuild changed nothing".
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives#rebuild-events</docs>
public interface IPerspectiveRowDigest {
  /// <summary>
  /// Digests the rows this perspective holds for <paramref name="streamIds"/>, or returns null when it cannot.
  /// </summary>
  /// <param name="perspectiveName">The perspective whose rows to digest, in CLR type-name form.</param>
  /// <param name="streamIds">The streams whose rows to include. An empty set yields null, not a digest.</param>
  /// <param name="ct">Cancellation token.</param>
  Task<string?> ComputeAsync(
      string perspectiveName, IReadOnlyCollection<Guid> streamIds, CancellationToken ct = default);
}
