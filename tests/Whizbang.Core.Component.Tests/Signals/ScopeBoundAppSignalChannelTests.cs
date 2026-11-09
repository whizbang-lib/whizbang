// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications.AppSignals;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Tests.Signals;

/// <summary>
/// #2076: the application's <see cref="IAppSignalChannel"/> is resolved per scope over the one bus. Resolved in a scope, a
/// publish carries the scope's open transaction (the first <see cref="IAppSignalTransactionSource"/> that reports one) to
/// the topic transports, which send the notification inside it. Resolved from the root (a singleton's dependency, or the
/// root provider itself), it carries none and the publish goes out at once, as before.
/// </summary>
/// <remarks>
/// Root detection compares the provider that resolves the channel with the one that resolves a singleton. Both are the
/// container's root scope in Microsoft.Extensions.DependencyInjection; the root tests below fail if a container release
/// ever hands a singleton factory something else.
/// </remarks>
/// <docs>fundamentals/work-coordinator/app-signals#transactional-publish</docs>
public class ScopeBoundAppSignalChannelTests {
  private sealed class RecordingTopicTransport : ISignalTransport, ISignalTopicTransport {
    public List<(string Topic, string Payload, DbTransaction? Transaction)> Published { get; } = [];
    public List<string> Subscribed { get; } = [];

    public Task StartAsync(ISignalSink sink, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target, CancellationToken cancellationToken = default)
      where TSignal : ISignal => ValueTask.CompletedTask;

    public Task PublishTopicAsync(string topic, string payload, DbTransaction? transaction, CancellationToken cancellationToken = default) {
      Published.Add((topic, payload, transaction));
      return Task.CompletedTask;
    }

    public IDisposable SubscribeTopic(string topic, Func<string, CancellationToken, Task> handler) {
      Subscribed.Add(topic);
      return new Nothing();
    }

    private sealed class Nothing : IDisposable {
      public void Dispose() { }
    }
  }

  /// <summary>A transaction no connection backs: the channel only carries it, it never runs a command on it.</summary>
  private sealed class MarkerTransaction : DbTransaction {
    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
    protected override DbConnection? DbConnection => null;
    public override void Commit() { }
    public override void Rollback() { }
  }

  /// <summary>A scoped source standing in for a framework DbContext; <see cref="Open"/> is what it reports.</summary>
  private sealed class FakeSource : IAppSignalTransactionSource {
    public DbTransaction? Open { get; set; }
    public int Reads { get; private set; }

    public DbTransaction? CurrentTransaction {
      get {
        Reads++;
        return Open;
      }
    }
  }

  private sealed class FirstSource : IAppSignalTransactionSource {
    public FakeSource Inner { get; } = new();
    public DbTransaction? CurrentTransaction => Inner.CurrentTransaction;
  }

  private sealed class SecondSource : IAppSignalTransactionSource {
    public FakeSource Inner { get; } = new();
    public DbTransaction? CurrentTransaction => Inner.CurrentTransaction;
  }

  private sealed class SingletonPublisher(IAppSignalChannel channel) {
    public IAppSignalChannel Channel { get; } = channel;
  }

  private static (ServiceProvider Provider, RecordingTopicTransport Transport) _build(Action<IServiceCollection>? extra = null) {
    var transport = new RecordingTopicTransport();
    var services = new ServiceCollection();
    services.AddSingleton(new SignalBus([transport], []));
    services.AddWhizbangAppSignalChannel();
    services.AddScoped<FirstSource>();
    services.AddScoped<SecondSource>();
    services.AddScoped<IAppSignalTransactionSource>(static sp => sp.GetRequiredService<FirstSource>());
    services.AddScoped<IAppSignalTransactionSource>(static sp => sp.GetRequiredService<SecondSource>());
    extra?.Invoke(services);
    // Scope validation on, as a Development host runs: resolving a scoped source from the root would throw here.
    var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    return (provider, transport);
  }

  [Test]
  public async Task InAScope_WithAnOpenTransaction_ThePublishCarriesItAsync() {
    var (provider, transport) = _build();
    await using var _ = provider;
    await using var scope = provider.CreateAsyncScope();
    var tx = new MarkerTransaction();
    scope.ServiceProvider.GetRequiredService<FirstSource>().Inner.Open = tx;

    await scope.ServiceProvider.GetRequiredService<IAppSignalChannel>().PublishAsync("orders", "1");

    await Assert.That(transport.Published).Count().IsEqualTo(1);
    await Assert.That(transport.Published[0].Transaction).IsSameReferenceAs(tx)
      .Because("a publish made inside the scope's transaction is sent inside it, so it is delivered only on commit");
  }

  [Test]
  public async Task InAScope_TheFirstSourceReportingATransactionWinsAsync() {
    var (provider, transport) = _build();
    await using var _ = provider;
    await using var scope = provider.CreateAsyncScope();
    var second = new MarkerTransaction();
    scope.ServiceProvider.GetRequiredService<SecondSource>().Inner.Open = second;

    await scope.ServiceProvider.GetRequiredService<IAppSignalChannel>().PublishAsync("orders", "1");

    await Assert.That(transport.Published[0].Transaction).IsSameReferenceAs(second)
      .Because("a source with no open transaction is passed over for the next one");

    var first = new MarkerTransaction();
    scope.ServiceProvider.GetRequiredService<FirstSource>().Inner.Open = first;
    await scope.ServiceProvider.GetRequiredService<IAppSignalChannel>().PublishAsync("orders", "2");
    await Assert.That(transport.Published[1].Transaction).IsSameReferenceAs(first)
      .Because("with two open, the first registered source is the one used");
  }

  [Test]
  public async Task InAScope_WithNoOpenTransaction_ThePublishCarriesNoneAsync() {
    var (provider, transport) = _build();
    await using var _ = provider;
    await using var scope = provider.CreateAsyncScope();

    await scope.ServiceProvider.GetRequiredService<IAppSignalChannel>().PublishAsync("orders", "1");

    await Assert.That(transport.Published[0].Transaction).IsNull()
      .Because("with no transaction open the publish goes out at once, as before");
    await Assert.That(scope.ServiceProvider.GetRequiredService<FirstSource>().Inner.Reads).IsEqualTo(1);
  }

  [Test]
  public async Task FromTheRoot_ThePublishCarriesNoTransaction_AndReadsNoScopedSourceAsync() {
    // Resolving a scoped source from the root throws under scope validation; this passing proves the channel knew it was
    // at the root and asked no source at all.
    var (provider, transport) = _build();
    await using var _ = provider;

    var channel = provider.GetRequiredService<IAppSignalChannel>();
    await channel.PublishAsync("orders", "1");

    await Assert.That(((ScopeBoundAppSignalChannel)channel).IsScopeBound).IsFalse();
    await Assert.That(transport.Published[0].Transaction).IsNull();
  }

  [Test]
  public async Task ASingletonsChannel_IsTheRootsEvenWhenFirstResolvedInsideAScopeAsync() {
    // The singleton holder of the root provider and a singleton consumer are both built on first resolution, which here
    // happens inside a scope: each must still see the root, not that scope.
    var (provider, transport) = _build(static s => s.AddSingleton<SingletonPublisher>());
    await using var _ = provider;
    await using var scope = provider.CreateAsyncScope();
    scope.ServiceProvider.GetRequiredService<FirstSource>().Inner.Open = new MarkerTransaction();

    var scoped = (ScopeBoundAppSignalChannel)scope.ServiceProvider.GetRequiredService<IAppSignalChannel>();
    var singleton = scope.ServiceProvider.GetRequiredService<SingletonPublisher>();
    await singleton.Channel.PublishAsync("orders", "from-singleton");

    await Assert.That(scoped.IsScopeBound).IsTrue();
    await Assert.That(((ScopeBoundAppSignalChannel)singleton.Channel).IsScopeBound).IsFalse()
      .Because("a singleton outlives every scope, so its channel must never read one");
    await Assert.That(transport.Published[0].Transaction).IsNull()
      .Because("a singleton's publish goes out at once even while some scope has a transaction open");
  }

  [Test]
  public async Task RepeatedResolutions_KeepTheSameRootDecisionAsync() {
    // The container compiles a service's resolution after its first few uses; the decision must not change with it.
    var (provider, _) = _build();
    await using var __ = provider;
    for (var i = 0; i < 6; i++) {
      await using var scope = provider.CreateAsyncScope();
      await Assert.That(((ScopeBoundAppSignalChannel)scope.ServiceProvider.GetRequiredService<IAppSignalChannel>()).IsScopeBound).IsTrue();
      await Assert.That(((ScopeBoundAppSignalChannel)provider.GetRequiredService<IAppSignalChannel>()).IsScopeBound).IsFalse();
    }
  }

  [Test]
  public async Task Subscribe_GoesToTheBus_AndTopicsAreValidatedAsBeforeAsync() {
    var (provider, transport) = _build();
    await using var _ = provider;
    await using var scope = provider.CreateAsyncScope();
    var channel = scope.ServiceProvider.GetRequiredService<IAppSignalChannel>();

    using var subscription = channel.Subscribe("orders", (_, _) => Task.CompletedTask);

    await Assert.That(transport.Subscribed).IsEquivalentTo(["orders"]);
    await Assert.That(async () => await channel.PublishAsync("wh_internal", "x")).Throws<ArgumentException>();
    await Assert.That(transport.Published).IsEmpty();
  }

  [Test]
  public async Task AddWhizbangAppSignalChannel_IsIdempotent_AndRequiresServicesAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangAppSignalChannel();
    services.AddWhizbangAppSignalChannel();

    await Assert.That(services.Count(d => d.ServiceType == typeof(IAppSignalChannel))).IsEqualTo(1);
    await Assert.That(services.Single(d => d.ServiceType == typeof(IAppSignalChannel)).Lifetime).IsEqualTo(ServiceLifetime.Transient)
      .Because("one channel per resolution, bound to the provider that resolved it");
    await Assert.That(() => ((IServiceCollection)null!).AddWhizbangAppSignalChannel()).Throws<ArgumentNullException>();
  }
}
