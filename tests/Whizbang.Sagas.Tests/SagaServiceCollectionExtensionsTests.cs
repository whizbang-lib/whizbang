// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Sagas;
using Whizbang.Sagas.Observability;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Locks <c>AddWhizbangSagas</c> DI registrations and the namespace
/// configuration path. The namespace override is the principal
/// migration mechanism for consumers with pre-existing per-item streams
/// (a consumer migration story); these tests pin that the override
/// actually takes effect on <see cref="SagaItemStreams.AppDefaultNamespace"/>
/// and that registered services round-trip cleanly.
/// </summary>
[Category("Unit")]
[Category("Saga")]
[NotInParallel("AppDefaultNamespace")]
public class SagaServiceCollectionExtensionsTests {

  // Each test must restore AppDefaultNamespace so subsequent tests in
  // the suite don't pick up a configured override unexpectedly.

  [Test]
  public async Task AddWhizbangSagas_NoConfigure_LeavesAppDefaultNamespaceAtFactoryDefaultAsync() {
    var prior = SagaItemStreams.AppDefaultNamespace;
    try {
      SagaItemStreams.AppDefaultNamespace = SagaItemStreams.DefaultNamespace;

      var services = new ServiceCollection();
      services.AddSingleton(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
      services.AddWhizbangSagas();

      await Assert.That(SagaItemStreams.AppDefaultNamespace)
        .IsEqualTo(SagaItemStreams.DefaultNamespace)
        .Because("Calling AddWhizbangSagas without a configure callback must not alter the namespace — fresh consumers get Whizbang's default unchanged.");
    } finally {
      SagaItemStreams.AppDefaultNamespace = prior;
    }
  }

  [Test]
  public async Task AddWhizbangSagas_WithCustomNamespace_AppliesItToAppDefaultNamespaceAsync() {
    var prior = SagaItemStreams.AppDefaultNamespace;
    try {
      var custom = Guid.Parse("0b36f8d4-3884-4c3c-b92b-fc6ec74775ea");

      var services = new ServiceCollection();
      services.AddSingleton(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
      services.AddWhizbangSagas(opts => opts.PerItemStreamNamespace = custom);

      await Assert.That(SagaItemStreams.AppDefaultNamespace).IsEqualTo(custom)
        .Because("The configured namespace must take effect immediately so every subsequent SagaItemStreams.Of(sagaId, itemId) call uses the consumer's value. This is the consumer migration story — one line at startup, no per-saga attribute pollution.");
    } finally {
      SagaItemStreams.AppDefaultNamespace = prior;
    }
  }

  [Test]
  public async Task AddWhizbangSagas_RegistersSagaOptionsAsync() {
    var prior = SagaItemStreams.AppDefaultNamespace;
    try {
      var custom = Guid.Parse("0b36f8d4-3884-4c3c-b92b-fc6ec74775ea");

      var services = new ServiceCollection();
      services.AddSingleton(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
      services.AddWhizbangSagas(opts => opts.PerItemStreamNamespace = custom);

      var sp = services.BuildServiceProvider();
      var opts = sp.GetRequiredService<SagaOptions>();

      await Assert.That(opts.PerItemStreamNamespace).IsEqualTo(custom)
        .Because("SagaOptions is the resolvable representation of the configured values; services depending on it must see the configured namespace.");
    } finally {
      SagaItemStreams.AppDefaultNamespace = prior;
    }
  }

  [Test]
  public async Task AddWhizbangSagas_RegistersSagaMetricsAsync() {
    var prior = SagaItemStreams.AppDefaultNamespace;
    try {
      var services = new ServiceCollection();
      services.AddSingleton(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
      services.AddWhizbangSagas();

      var sp = services.BuildServiceProvider();
      var metrics = sp.GetRequiredService<SagaMetrics>();

      await Assert.That(metrics).IsNotNull()
        .Because("SagaMetrics is the standard observability surface every saga emits into; if it's not registered, every consumer would silently lose telemetry.");
    } finally {
      SagaItemStreams.AppDefaultNamespace = prior;
    }
  }

  /// <summary>
  /// A consumer's own saga emitter, registered before <c>AddWhizbangSagas</c>, is the one saga services
  /// get; the framework's dispatcher-backed emitter is only the default.
  /// </summary>
  [Test]
  public async Task AddWhizbangSagas_ConsumerEmitterRegisteredFirst_IsKeptAsync() {
    var prior = SagaItemStreams.AppDefaultNamespace;
    try {
      var consumerEmitter = new ConsumerEmitter();
      var services = new ServiceCollection();
      services.AddSingleton<Whizbang.Sagas.Services.ISagaEventEmitter>(consumerEmitter);
      services.AddWhizbangSagas();

      await using var sp = services.BuildServiceProvider();
      await using var scope = sp.CreateAsyncScope();

      await Assert.That(scope.ServiceProvider.GetRequiredService<Whizbang.Sagas.Services.ISagaEventEmitter>()).IsSameReferenceAs(consumerEmitter)
        .Because("registering the framework default over it silently replaced the consumer's emitter");
    } finally {
      SagaItemStreams.AppDefaultNamespace = prior;
    }
  }

  /// <summary>With no emitter of the consumer's own, saga services publish through the dispatcher.</summary>
  [Test]
  public async Task AddWhizbangSagas_NoEmitterRegistered_RegistersTheDispatcherBackedOneAsync() {
    var prior = SagaItemStreams.AppDefaultNamespace;
    try {
      var services = new ServiceCollection();
      services.AddWhizbangSagas();

      var registration = services.Single(d => d.ServiceType == typeof(Whizbang.Sagas.Services.ISagaEventEmitter));

      await Assert.That(registration.ImplementationType).IsEqualTo(typeof(Whizbang.Sagas.Services.DispatcherSagaEventEmitter));
      await Assert.That(registration.Lifetime).IsEqualTo(ServiceLifetime.Scoped);
    } finally {
      SagaItemStreams.AppDefaultNamespace = prior;
    }
  }

  private sealed class ConsumerEmitter : Whizbang.Sagas.Services.ISagaEventEmitter {
    public Task PublishAsync<TEvent>(TEvent eventData) where TEvent : Whizbang.Core.IEvent => Task.CompletedTask;
    public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken)
      where TEvent : Whizbang.Core.IEvent => Task.FromResult(true);
  }

  [Test]
  public async Task AddWhizbangSagas_NullServices_ThrowsAsync() {
    await Assert.That(() => ((IServiceCollection)null!).AddWhizbangSagas())
      .ThrowsExactly<ArgumentNullException>();
  }
}
