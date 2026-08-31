using System.Text.Json;
using Azure.Messaging.ServiceBus.Administration;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.AzureServiceBus.Integration.Tests.Containers;

namespace Whizbang.Transports.AzureServiceBus.Integration.Tests;

/// <summary>
/// Covers <c>PeekBacklogsAsync</c> — the backlog-age sample that distinguishes a deep-but-young
/// queue draining normally from a shallow-but-ancient one whose consumer has stopped.
/// </summary>
/// <remarks>
/// Depth comes from a management call the emulator does not serve, so the admin client is faked
/// for that one number. The oldest-enqueued peek is a data-plane operation the emulator does
/// serve, and that is the half worth exercising for real — a peek that silently returned nothing
/// would make every backlog look brand new, which is exactly the signal this exists to provide.
/// </remarks>
[Category("Integration")]
[Timeout(240_000)]
[NotInParallel("AsbBacklogPeek")]
[ClassDataSource<ServiceBusEmulatorFixtureSource>(Shared = SharedType.PerAssembly)]
public class AzureServiceBusBacklogPeekIntegrationTests(ServiceBusEmulatorFixtureSource fixtureSource) {
  private readonly ServiceBusEmulatorFixture _fixture = fixtureSource.Fixture;
  private readonly List<IAsyncDisposable> _disposables = [];

  [After(Test)]
  public async Task DisposeAsync() {
    foreach (var d in _disposables) {
      try { await d.DisposeAsync(); } catch { /* best-effort cleanup */ }
    }
    _disposables.Clear();
  }

  /// <summary>Serves only the depth call; every other admin member throws so a stray use shows up.</summary>
  private sealed class DepthOnlyAdminClient(long depth, Exception? depthThrows = null) : IServiceBusAdminClient {
    public Task<long> GetSubscriptionActiveMessageCountAsync(
        string topicName, string subscriptionName, CancellationToken ct = default)
      => depthThrows is not null
        ? Task.FromException<long>(depthThrows)
        : Task.FromResult(depth);

    public Task<NamespaceProperties> GetNamespacePropertiesAsync(CancellationToken ct = default)
      => throw new NotImplementedException();
    public Task<bool> TopicExistsAsync(string topicName, CancellationToken ct = default)
      => Task.FromResult(true);
    public Task CreateTopicAsync(string topicName, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> SubscriptionExistsAsync(string t, string s, CancellationToken ct = default)
      => Task.FromResult(true);
    public Task CreateSubscriptionAsync(string t, string s, int m, TimeSpan l, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task CreateSubscriptionAsync(string t, string s, bool r, int m, TimeSpan l, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task UpdateSubscriptionLockDurationAsync(string t, string s, TimeSpan l, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task<SubscriptionProperties> GetSubscriptionAsync(string t, string s, CancellationToken ct = default)
      => throw new NotImplementedException();
    public Task DeleteSubscriptionAsync(string t, string s, CancellationToken ct = default) => Task.CompletedTask;
    public IAsyncEnumerable<RuleProperties> GetRulesAsync(string t, string s, CancellationToken ct = default)
      => throw new NotImplementedException();
    public Task DeleteRuleAsync(string t, string s, string r, CancellationToken ct = default) => Task.CompletedTask;
    public Task CreateRuleAsync(string t, string s, CreateRuleOptions o, CancellationToken ct = default)
      => Task.CompletedTask;
  }

  private AzureServiceBusTransport _transport(IServiceBusAdminClient? admin) {
    var transport = new AzureServiceBusTransport(
      _fixture.Client,
      new JsonSerializerOptions(),
      adminClient: admin);
    _disposables.Add(transport);
    return transport;
  }

  [Test]
  public async Task PeekBacklogs_WithoutAnAdminClient_ReportsNothingAsync() {
    // No admin client means no watchdog and no depth source; the sample set is empty rather
    // than a list of entities with unknown depth.
    var transport = _transport(admin: null);

    var samples = await transport.PeekBacklogsAsync(CancellationToken.None);

    await Assert.That(samples).IsEmpty();
  }

  [Test]
  public async Task PeekBacklogs_WithNoTrackedEntities_ReportsNothingAsync() {
    var transport = _transport(new DepthOnlyAdminClient(depth: 5));

    var samples = await transport.PeekBacklogsAsync(CancellationToken.None);

    await Assert.That(samples).IsEmpty();
  }

  [Test]
  public async Task PeekBacklogs_ReadsTheOldestEnqueuedTimeFromTheBrokerAsync() {
    // The peek is real: a message is published, then its enqueued time comes back through
    // the broker rather than through a stub.
    var transport = _transport(new DepthOnlyAdminClient(depth: 1));
    transport.LivenessWatchdog!.Track("topic-00", "sub-00-a");

    var sender = _fixture.Client.CreateSender("topic-00");
    await using (sender) {
      await sender.SendMessageAsync(new Azure.Messaging.ServiceBus.ServiceBusMessage("probe"));
    }

    var samples = await transport.PeekBacklogsAsync(CancellationToken.None);

    await Assert.That(samples).HasSingleItem();
    await Assert.That(samples[0].Entity).IsEqualTo("topic-00/sub-00-a");
    await Assert.That(samples[0].Depth).IsEqualTo(1);
    await Assert.That(samples[0].OldestAge).IsNotNull()
      .Because("a peek that returned nothing would make every backlog look brand new, "
             + "which is the one thing this sample exists to disprove");
  }

  [Test]
  public async Task PeekBacklogs_WhenTheDepthCallFails_SkipsThatEntityAsync() {
    // An entity that cannot be read right now is skipped, not reported with a wrong depth;
    // the next tick retries it.
    var transport = _transport(new DepthOnlyAdminClient(0, new InvalidOperationException("unreadable")));
    transport.LivenessWatchdog!.Track("topic-00", "sub-00-a");

    var samples = await transport.PeekBacklogsAsync(CancellationToken.None);

    await Assert.That(samples).IsEmpty();
  }

  [Test]
  public async Task PeekBacklogs_WithAProbeOverride_UsesItInsteadOfTheBrokerAsync() {
    // The override exists so a host can supply a cheaper age source than a peek.
    var transport = _transport(new DepthOnlyAdminClient(depth: 7));
    transport.LivenessWatchdog!.Track("topic-00", "sub-00-a");
    var stamped = DateTimeOffset.UtcNow.AddMinutes(-30);
    transport.OldestEnqueuedTimeProbe = (_, _, _) => Task.FromResult<DateTimeOffset?>(stamped);

    var samples = await transport.PeekBacklogsAsync(CancellationToken.None);

    await Assert.That(samples).HasSingleItem();
    await Assert.That(samples[0].Depth).IsEqualTo(7);
    await Assert.That(samples[0].OldestAge).IsNotNull();
    await Assert.That(samples[0].OldestAge!.Value.TotalMinutes).IsGreaterThan(25);
  }

  [Test]
  public async Task PeekBacklogs_WhenTheProbeFails_StillReportsDepthAsync() {
    // Age is the richer signal but depth alone is still worth reporting — losing the whole
    // sample because the peek failed would hide a growing backlog entirely.
    var transport = _transport(new DepthOnlyAdminClient(depth: 3));
    transport.LivenessWatchdog!.Track("topic-00", "sub-00-a");
    transport.OldestEnqueuedTimeProbe =
      (_, _, _) => Task.FromException<DateTimeOffset?>(new InvalidOperationException("probe failed"));

    var samples = await transport.PeekBacklogsAsync(CancellationToken.None);

    await Assert.That(samples).HasSingleItem();
    await Assert.That(samples[0].Depth).IsEqualTo(3);
    await Assert.That(samples[0].OldestAge).IsNull();
  }
}
