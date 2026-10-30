// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The kind bits a direct append (<c>IEventStore.AppendAsync</c>, <c>wh_append_event</c>) stores with an event: the
/// <see cref="EventFlags.Ephemeral"/> or <see cref="EventFlags.Compacted"/> flag the dispatch path stamps on the same
/// payload. The mixed-stream guard (WHIZ133) reads them on every path, so a directly appended event must carry them. The
/// <see cref="EventFlags.Terminal"/> bit is kept too: storing a terminal event seals its stream (WS-3 S4).
/// </summary>
/// <remarks>
/// Only the kind bits are kept. The routing bits (<see cref="EventFlags.Collective"/>, <see cref="EventFlags.Composite"/>)
/// drive work the emit chains create and a direct append does not, so storing them here would claim routing that
/// never happened.
/// </remarks>
/// <docs>operations/diagnostics/whiz133</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DirectAppendKindTests.cs</tests>
internal static class DirectAppendKind {
  private const EventFlags KIND_BITS = EventFlags.Ephemeral | EventFlags.Compacted | EventFlags.Terminal;

  /// <summary>The kind bits of <paramref name="payload"/>, as an integer for the <c>p_flags</c> parameter.</summary>
  /// <param name="payload">The appended payload.</param>
  /// <param name="ephemeralModeResolver">Resolves <c>[Ephemeral]</c> declared on a type, when one is registered.</param>
  /// <returns><c>8</c> for an ephemeral event, <c>16</c> for a compaction origin, plus <c>64</c> for a terminal event;
  /// otherwise <c>0</c>.</returns>
  internal static int Of(object? payload, IEphemeralModeResolver? ephemeralModeResolver) =>
    (int)(EventFlagsDeriver.Derive(payload, null, null, ephemeralModeResolver) & KIND_BITS);
}
