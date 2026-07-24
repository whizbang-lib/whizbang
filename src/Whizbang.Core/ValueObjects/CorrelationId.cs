using System.Diagnostics;
using Whizbang.Core;

namespace Whizbang.Core.ValueObjects;

/// <summary>
/// Groups related messages together across distributed operations.
/// All messages in a logical workflow share the same CorrelationId.
/// Internally-minted ids use UUIDv7 (time-ordered, database-friendly) and align to the ambient W3C trace
/// context when one is active; externally-supplied ids (inbound headers, W3C trace-ids, non-v7 UUIDs) are
/// accepted verbatim via <see cref="FromExternal"/>.
/// </summary>
/// <docs>fundamentals/messages/message-context</docs>
/// <tests>tests/Whizbang.Core.Tests/ValueObjects/IdentityValueObjectTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/ValueObjects/CorrelationIdW3CTests.cs</tests>
[WhizbangId]
public readonly partial struct CorrelationId {
  /// <summary>
  /// Creates a correlation id aligned to the ambient W3C trace context: adopts the active <see
  /// cref="Activity"/>'s 128-bit <c>TraceId</c> when one exists (so Whizbang correlation lines up with the
  /// OpenTelemetry trace), otherwise mints a fresh UUIDv7. Use when starting a new root flow.
  /// </summary>
  public static CorrelationId NewRootAligned() {
    if (Activity.Current is { } activity && activity.TraceId != default) {
      Span<byte> traceBytes = stackalloc byte[16];
      activity.TraceId.CopyTo(traceBytes);
      return new CorrelationId(new Guid(traceBytes, bigEndian: true));
    }

    return New();
  }

  /// <summary>
  /// Wraps an externally-supplied 128-bit value — an inbound header token, a W3C trace-id, or any UUID —
  /// without the UUIDv7 validation that <see cref="From(System.Guid)"/> enforces for internally-minted ids.
  /// A correlation id can originate outside the system, so it need not be time-ordered.
  /// </summary>
  public static CorrelationId FromExternal(Guid value) => new(value);
}
