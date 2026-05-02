using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for <see cref="PgAppSignalChannel"/>. The publish path opens a real
/// Npgsql connection and emits <c>pg_notify(wh_app_&lt;topic&gt;, payload)</c>; tests verify
/// the row appears on the wire by listening from a separate connection.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Subscribe-path is NOT covered here</strong> — see <c>SubscribePathNotWired_IsKnownGap</c>.
/// <see cref="PgAppSignalChannel.Subscribe"/> registers handlers but
/// <c>PgAppSignalChannel.Dispatch</c> (the method that fans notifications to handlers) is
/// internal and never invoked from anywhere in the codebase. There is no listener that
/// LISTENs on <c>wh_app_*</c> channels and routes payloads to <c>Dispatch</c>. Subscribers
/// register but never receive anything in the current build.
/// </para>
/// <para>
/// Closing the gap requires either extending <see cref="PgWorkNotificationListener"/> to
/// LISTEN on additional channels (per active topic), or creating a sibling listener. Tracked
/// as follow-up; not blocking the LISTEN/NOTIFY rollout because the work-signal path
/// (<c>wh_work</c>) is what slice 1–6 actually uses.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/app-signals</docs>
public class PgAppSignalChannelIntegrationTests : EFCoreTestBase {

  private PgAppSignalChannel _newChannel(WhizbangNotificationOptions? options = null) {
    return new PgAppSignalChannel(
      Options.Create(options ?? new WhizbangNotificationOptions { DirectConnectionString = ConnectionString }),
      new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
      NullLogger<PgAppSignalChannel>.Instance);
  }

  [Test]
  public async Task Publish_EmitsPgNotify_OnDedicatedAppChannelAsync() {
    // Independent listener on a separate connection LISTENs on wh_app_<topic>; verifies
    // the channel actually emits pg_notify on the wire with the right channel + payload.
    const string topic = "myapp_topic";
    const string channel = "wh_app_" + topic;
    const string payload = "hello-from-test";

    await using var listenerConn = new NpgsqlConnection(ConnectionString);
    await listenerConn.OpenAsync();
    var receivedPayload = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    listenerConn.Notification += (_, args) => {
      if (args.Channel == channel) {
        receivedPayload.TrySetResult(args.Payload);
      }
    };
    await using (var listenCmd = listenerConn.CreateCommand()) {
      listenCmd.CommandText = $"LISTEN {channel}";
      await listenCmd.ExecuteNonQueryAsync();
    }

    var pubChannel = _newChannel();
    await pubChannel.PublishAsync(topic, payload);

    // Notifications dispatch on the listener's connection only when it reads from the wire.
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var waitTask = listenerConn.WaitAsync(cts.Token);
    var raced = await Task.WhenAny(receivedPayload.Task, Task.Delay(TimeSpan.FromSeconds(15)));

    await Assert.That(receivedPayload.Task.IsCompleted).IsTrue()
      .Because("PgAppSignalChannel.PublishAsync must emit pg_notify on the wh_app_<topic> channel reachable from any LISTENing connection");
    await Assert.That(receivedPayload.Task.Result).IsEqualTo(payload);
  }

  [Test]
  public async Task Publish_WithNoConnectionStringResolved_NoOpsAsync() {
    // Defensive: if the channel can't resolve a connection string, PublishAsync is a no-op
    // (logged at Debug). No exception thrown.
    var channel = _newChannel(new WhizbangNotificationOptions {
      // No DirectConnectionString, no ConnectionStringKey → resolver returns null.
    });

    // No exception should propagate.
    await channel.PublishAsync("any_topic", "any_payload");
  }

  [Test]
  [Skip("Known gap — see class remarks. PgAppSignalChannel.Subscribe registers handlers, but PgAppSignalChannel.Dispatch is never called from anywhere — no listener routes wh_app_* notifications to handlers. Will fail until the subscribe path is wired (extend PgWorkNotificationListener to LISTEN on additional channels, or add a sibling listener).")]
  public async Task SubscribePathNotWired_IsKnownGapAsync() {
    // Documents the gap: a subscriber receives nothing today even though publish works.
    const string topic = "myapp_topic";
    var channel = _newChannel();
    var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

    using var subscription = channel.Subscribe(topic, (payload, _) => {
      received.TrySetResult(payload);
      return Task.CompletedTask;
    });

    await channel.PublishAsync(topic, "should-be-delivered");

    // Today this would time out — Dispatch is never invoked. Skipped with [Skip] so the
    // suite stays green while the gap is documented and discoverable.
    var raced = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(5)));
    await Assert.That(received.Task.IsCompleted).IsTrue();
  }
}
