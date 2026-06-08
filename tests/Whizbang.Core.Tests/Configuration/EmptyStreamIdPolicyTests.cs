using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;

namespace Whizbang.Core.Tests.Configuration;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// Locks the v0.657 invariants around <see cref="EmptyStreamIdPolicy"/> — the global
/// knob governing how producers + drainers handle outbox / inbox / perspective rows
/// whose stream_id is the all-zeros UUID (<c>Guid.Empty</c>).
/// </summary>
/// <remarks>
/// Motivation: slot-3 forensic (Jun 2026) — a JDX producer wrote a
/// <c>RemoveShellUserCommand</c> outbox row with <c>stream_id =
/// 00000000-0000-0000-0000-000000000000</c> (zero-UUID, not NULL). The C#
/// coordinator's <c>r.StreamId ?? r.WorkId</c> fallback only catches NULL; Empty
/// slipped through and was filtered by the <c>Where(g => g != Guid.Empty)</c>
/// guard. ClaimWorker bumped attempts every cycle (996+) but never wrote to the
/// drain channel, so the row was silently stuck forever — no DLQ promotion, no
/// error, no log. This policy enum is the structural fix.
/// </remarks>
/// <docs>operations/configuration/empty-stream-id-policy</docs>
public class EmptyStreamIdPolicyTests {

  /// <summary>
  /// Locks the enum surface. Adding a new value is fine; removing or renumbering an
  /// existing one is a breaking change for operators with configuration files
  /// referencing them.
  /// </summary>
  [Test]
  public async Task EmptyStreamIdPolicy_HasExpectedValuesAsync() {
#pragma warning disable TUnitAssertions0005
    await Assert.That((int)EmptyStreamIdPolicy.Reject).IsEqualTo(0)
      .Because("Reject is the secure default — producers can't introduce zero-UUID rows; storage-time SQL guard throws.");
    await Assert.That((int)EmptyStreamIdPolicy.FallbackToMessageId).IsEqualTo(1)
      .Because("Lenient mode for migrations — storage accepts the row, coordinator coalesces Empty→message_id, Warning emitted.");
    await Assert.That((int)EmptyStreamIdPolicy.DeadLetter).IsEqualTo(2)
      .Because("Move-to-DLQ semantics for forensic preservation of bad rows without retry-spin.");
    await Assert.That((int)EmptyStreamIdPolicy.Purge).IsEqualTo(3)
      .Because("Hard-purge for known-spammy producers — DELETE the row, Error log with the distinct reason code.");
#pragma warning restore TUnitAssertions0005
  }
}
