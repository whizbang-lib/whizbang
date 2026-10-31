// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Attributes;

/// <summary>
/// Marks a perspective as <strong>overwrite-only</strong>: every <c>Apply</c> builds the row from its event alone and
/// never reads the current model, so the row after any event is that event's (#1869).
/// </summary>
/// <remarks>
/// <para>
/// A rewind of an overwrite-only perspective (a late event, an event below the row's position, a refused write, a row
/// that wakes after eviction) applies only the stream's last event, taken in the order a full replay folds the stream,
/// instead of loading and applying every event to a model only the last one decides. The receptors of the events it
/// skips do not run: a rewind rebuilds state, it does not re-deliver events.
/// </para>
/// <para>
/// The rewind replays the stream in full instead when the last event cannot decide the row alone: when the stream holds
/// an event whose <c>Apply</c> can purge or return an action (<c>ApplyResult&lt;T&gt;</c>, <c>ModelAction</c>, a tuple),
/// when the last event's <c>Apply</c> returns no model or throws, and when the stream starts purged. The row written is
/// always the row a full replay writes.
/// </para>
/// <para>
/// The marker is explicit; the generator never infers it. An <c>Apply</c> of a marked perspective that reads its
/// current-model parameter contradicts it, and the analyzer reports it (WHIZ108).
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/rewind#overwrite-only</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/OverwriteOnlyRewindTests.cs</tests>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class OverwriteOnlyAttribute : Attribute;
