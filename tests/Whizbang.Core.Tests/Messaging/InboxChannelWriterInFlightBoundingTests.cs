using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The in-flight set tracks messages handed to the channel so they are not re-offered while queued.
/// Nothing ever removes an entry on the SUCCESS path — completion runs through the completion
/// channel, and <c>RemoveInFlight</c> means "abandoned without completing" — so the set grows for
/// the lifetime of the process.
/// </summary>
/// <remarks>
/// <para>
/// Two consequences, both silent. It is an unbounded memory leak on any long-lived service; and
/// <c>ShouldRenewLease</c> keeps returning true for work that finished long ago, so leases are
/// renewed for rows nobody is processing.
/// </para>
/// <para>
/// Entries older than the lease cannot legitimately be in flight — the lease has lapsed and the
/// store will re-issue those rows to whoever claims them next — so ageing them out is safe and
/// bounds the set. It also means a flag stranded by a hung or cancelled task stops blocking that
/// message forever, which is the failure mode that made an earlier in-memory filter on this path
/// unrecoverable without a process restart.
/// </para>
/// </remarks>
[Category("Messaging")]
public class InboxChannelWriterInFlightBoundingTests {

  [Test]
  public async Task InFlightEntries_OlderThanTheLease_StopBeingTrackedAsync() {
    var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
    var writer = new InboxChannelWriter(clock);
    var stale = _work();

    await writer.WriteAsync(stale);
    await Assert.That(writer.IsInFlight(stale.MessageId)).IsTrue()
      .Because("freshly handed-off work must be tracked, or it could be offered twice");

    // Past any plausible lease. A row held this long is no longer ours — the lease lapsed and the
    // store will re-issue it — so continuing to track it serves no purpose and never ends.
    clock.Advance(TimeSpan.FromHours(2));
    await writer.WriteAsync(_work());

    await Assert.That(writer.IsInFlight(stale.MessageId)).IsFalse()
      .Because("an entry older than the lease cannot legitimately be in flight; keeping it leaks "
             + "memory for the life of the process and renews leases for work nobody is doing");
  }

  [Test]
  public async Task RecentInFlightEntries_AreNotEvictedAsync() {
    var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
    var writer = new InboxChannelWriter(clock);
    var recent = _work();

    await writer.WriteAsync(recent);
    clock.Advance(TimeSpan.FromSeconds(5));
    await writer.WriteAsync(_work());

    // Eviction must only remove work whose lease has genuinely lapsed. Dropping live entries would
    // let the same message be offered twice while the first copy is still being processed.
    await Assert.That(writer.IsInFlight(recent.MessageId)).IsTrue()
      .Because("work still inside its lease is legitimately in flight — evicting it would allow a "
             + "duplicate offer while the original is still being processed");
  }

  private static InboxWork _work() {
    var messageId = MessageId.New();
    return new InboxWork {
      MessageId = messageId.Value,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = messageId,
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        Hops = [],
        Payload = JsonDocument.Parse("{}").RootElement
      },
      MessageType = "test-message"
    };
  }
}
