namespace Whizbang.Core.Attributes;

/// <summary>
/// Marks a perspective as a <strong>full-history projection</strong> — one that needs every event in its
/// streams and <em>cannot</em> resume from a carry-forward / closing event alone (e.g. a per-transaction
/// ledger list, as opposed to a running balance). A1 "close the books" uses this as a safety guard: a
/// <em>discard</em>-close (truncate without archiving) is refused for any stream a full-history projection
/// consumes, because discarding the detail would make that projection unrebuildable. An archiving close
/// (<c>archive: true</c>) stays allowed — the detail is retrievable from cold storage.
/// </summary>
/// <remarks>
/// Default (unmarked) perspectives are assumed <em>resumable</em> — they can rebuild from the closing event
/// forward, which is the common "closing the books" case. Add <c>[FullHistory]</c> only to projections that
/// genuinely need the full detail. Resolved at compile time (the perspective-runner generator registers it),
/// enforced at runtime by <see cref="Whizbang.Core.Lifecycle.IStreamCloser"/> — a Roslyn analyzer cannot
/// decide it statically (the target stream, and which perspectives consume it, are runtime values).
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events</docs>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class FullHistoryAttribute : Attribute;
