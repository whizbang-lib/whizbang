// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.MultiStream;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.MultiStream;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests.MultiStream;

/// <summary>
/// The Postgres notifications wire multi-stream commands (#1358): the Postgres store over the null default whichever
/// order the application and the driver register in, the worker, and its subscription to new-run notices. Composition
/// only: nothing starts.
/// </summary>
/// <docs>fundamentals/dispatcher/multi-stream-commands#registration</docs>
[Category("Shard3")]
public class MultiStreamCommandPostgresRegistrationTests {
  private static ServiceProvider _build(Action<IServiceCollection>? before = null, Action<IServiceCollection>? after = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    services.AddLogging();
    services.AddSingleton<IServiceInstanceProvider, ServiceInstanceProvider>();
    services.AddSingleton<IWorkNotificationListener, NoOpWorkNotificationListener>();
    services.AddSingleton<ISchemaReadyGate>(SchemaReadyGate.AlreadyReady());
    before?.Invoke(services);
    services.AddWhizbangPostgresNotifications();
    after?.Invoke(services);
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task AddWhizbangPostgresNotifications_WiresTheStoreAndTheWorkerAsync() {
    await using var sp = _build();

    await Assert.That(sp.GetRequiredService<IMultiStreamCommandStore>()).IsTypeOf<PgMultiStreamCommandStore>();
    var hosted = sp.GetServices<IHostedService>().ToList();
    await Assert.That(hosted.OfType<MultiStreamCommandWorker>().Single()).IsSameReferenceAs(sp.GetRequiredService<MultiStreamCommandWorker>());
    await Assert.That(hosted.OfType<MultiStreamCommandSubscriber>().Count()).IsEqualTo(1);
  }

  [Test]
  public async Task AHandlerRegisteredBeforeTheDriver_StillGetsThePostgresStoreAsync() {
    await using var sp = _build(before: s => s.AddMultiStreamCommandHandler<RegistrationProbeCommand, RegistrationProbeHandler>());

    await Assert.That(sp.GetRequiredService<IMultiStreamCommandStore>()).IsTypeOf<PgMultiStreamCommandStore>()
      .Because("the handler's null default must give way to the driver's store, or every command would be refused");
  }

  [Test]
  public async Task AHandlerRegisteredAfterTheDriver_KeepsThePostgresStoreAsync() {
    await using var sp = _build(after: s => s.AddMultiStreamCommandHandler<RegistrationProbeCommand, RegistrationProbeHandler>());

    await Assert.That(sp.GetRequiredService<IMultiStreamCommandStore>()).IsTypeOf<PgMultiStreamCommandStore>();
  }

  [Test]
  public async Task Subscriber_SubscribesTheWorkerToNewRunNoticesUntilStoppedAsync() {
    var connection = new RecordingNotifyConnection();
    await using var sp = _build(after: s => s.AddSingleton<ISharedNotifyConnection>(connection));
    var subscriber = sp.GetServices<IHostedService>().OfType<MultiStreamCommandSubscriber>().Single();

    await subscriber.StartAsync(CancellationToken.None);
    var subscribed = connection.Subscriptions.Single();
    await subscriber.StopAsync(CancellationToken.None);
    await subscriber.StopAsync(CancellationToken.None);

    await Assert.That(subscribed.Subscription).IsSameReferenceAs(sp.GetRequiredService<MultiStreamCommandWorker>());
    await Assert.That(subscribed.Subscription.ChannelName).IsEqualTo(PgMultiStreamCommandStore.CHANNEL);
    await Assert.That(subscribed.Disposed).IsTrue();
  }

  [Test]
  public async Task Options_BindFromTheirSectionAsync() {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:MultiStreamCommands:Enabled"] = "false",
      ["Whizbang:MultiStreamCommands:BatchSize"] = "12",
      ["Whizbang:MultiStreamCommands:Lease"] = "00:02:00",
      ["Whizbang:MultiStreamCommands:MaxAttempts"] = "5",
      ["Whizbang:MultiStreamCommands:PollInterval"] = "00:00:10",
      ["Whizbang:MultiStreamCommands:CompletedRunRetention"] = "2.00:00:00",
      ["Whizbang:MultiStreamCommands:PurgeInterval"] = "00:30:00",
      ["Whizbang:MultiStreamCommands:NoHandlerRetryDelay"] = "00:00:20"
    }).Build();
    await using var sp = _build(after: s => s.AddSingleton<IConfiguration>(configuration));

    var options = sp.GetRequiredService<IOptions<MultiStreamCommandOptions>>().Value;

    await Assert.That(options.Enabled).IsFalse();
    await Assert.That(options.BatchSize).IsEqualTo(12);
    await Assert.That(options.Lease).IsEqualTo(TimeSpan.FromMinutes(2));
    await Assert.That(options.MaxAttempts).IsEqualTo(5);
    await Assert.That(options.PollInterval).IsEqualTo(TimeSpan.FromSeconds(10));
    await Assert.That(options.CompletedRunRetention).IsEqualTo(TimeSpan.FromDays(2));
    await Assert.That(options.PurgeInterval).IsEqualTo(TimeSpan.FromMinutes(30));
    await Assert.That(options.NoHandlerRetryDelay).IsEqualTo(TimeSpan.FromSeconds(20));
  }

  [Test]
  public async Task Options_AValueThatDoesNotParse_KeepsItsDefaultAsync() {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:MultiStreamCommands:Enabled"] = "maybe",
      ["Whizbang:MultiStreamCommands:BatchSize"] = "many",
      ["Whizbang:MultiStreamCommands:Lease"] = "soon",
      ["Whizbang:MultiStreamCommands:MaxAttempts"] = "lots",
      ["Whizbang:MultiStreamCommands:PollInterval"] = "often",
      ["Whizbang:MultiStreamCommands:CompletedRunRetention"] = "forever",
      ["Whizbang:MultiStreamCommands:PurgeInterval"] = "sometimes",
      ["Whizbang:MultiStreamCommands:NoHandlerRetryDelay"] = "later"
    }).Build();
    await using var sp = _build(after: s => s.AddSingleton<IConfiguration>(configuration));

    var options = sp.GetRequiredService<IOptions<MultiStreamCommandOptions>>().Value;
    var defaults = new MultiStreamCommandOptions();

    await Assert.That(options.Enabled).IsEqualTo(defaults.Enabled);
    await Assert.That(options.BatchSize).IsEqualTo(defaults.BatchSize);
    await Assert.That(options.Lease).IsEqualTo(defaults.Lease);
    await Assert.That(options.MaxAttempts).IsEqualTo(defaults.MaxAttempts);
    await Assert.That(options.PollInterval).IsEqualTo(defaults.PollInterval);
    await Assert.That(options.CompletedRunRetention).IsEqualTo(defaults.CompletedRunRetention);
    await Assert.That(options.PurgeInterval).IsEqualTo(defaults.PurgeInterval);
    await Assert.That(options.NoHandlerRetryDelay).IsEqualTo(defaults.NoHandlerRetryDelay);
  }

  [Test]
  public async Task Options_WithNoSection_AreTheDefaultsAsync() {
    await using var sp = _build();

    await Assert.That(sp.GetRequiredService<IOptions<MultiStreamCommandOptions>>().Value.BatchSize)
      .IsEqualTo(new MultiStreamCommandOptions().BatchSize);
  }

  public sealed record RegistrationProbeCommand(Guid RunId, IReadOnlyList<Guid> StreamIds) : IMultiStreamCommand;

  public sealed class RegistrationProbeHandler : IMultiStreamCommandHandler<RegistrationProbeCommand> {
    public ValueTask HandleStreamAsync(RegistrationProbeCommand command, Guid streamId, CancellationToken cancellationToken) =>
      ValueTask.CompletedTask;
  }

  private sealed class RecordingNotifyConnection : ISharedNotifyConnection {
    public List<RecordedSubscription> Subscriptions { get; } = [];

    public IDisposable Subscribe(INotifySubscription subscription) {
      var recorded = new RecordedSubscription(subscription);
      Subscriptions.Add(recorded);
      return recorded;
    }
  }

  private sealed class RecordedSubscription(INotifySubscription subscription) : IDisposable {
    public INotifySubscription Subscription { get; } = subscription;

    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;
  }
}
