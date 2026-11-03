// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Every integrity receptor this driver registers is registered deferred, at every stage (#2001). A receptor added
/// for one of these messages without <see cref="IntegrityDeliveryRegistration.RegisterIntegrityReceptor{TMessage}"/>
/// would run its integrity work inline on whichever instance received the message, alongside ordinary traffic and
/// outside the idle gate, which is the load this change exists to remove. This test fails naming it.
/// </summary>
/// <remarks>
/// The registrars are found by shape, as <see cref="BuiltInReceptorRegistrarNullDefaultTests"/> finds them, so a new
/// registrar is covered the moment it is written. The integrity messages are named here, and the converse check (each
/// one is registered at all three stages) keeps the list from going stale: a message that stops being registered, or a
/// stage that goes missing, fails too.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IntegrityCheckpointReceptorRegistrar.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IntegrityManifestReceptors.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/RedeliveryRequestReceptorRegistrar.cs</code-under-test>
/// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
public class IntegrityReceptorRegistrationEnforcementTests {
  private static readonly Type[] _integrityMessages = [
    typeof(IntegrityCheckpoint), typeof(RequestIntegrityManifest), typeof(IntegrityManifest), typeof(RequestRedeliveryCommand),
  ];

  private static readonly LifecycleStage[] _stages =
    [LifecycleStage.LocalImmediateInline, LifecycleStage.PreOutboxInline, LifecycleStage.PostInboxInline];

  private sealed class RecordingRegistry : IReceptorRegistry {
    public List<(Type Msg, LifecycleStage Stage, object Receptor)> Registered { get; } = [];
    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage =>
      Registered.Add((typeof(TMessage), stage, receptor));
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage =>
      Registered.Add((typeof(TMessage), stage, receptor));
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => [];
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  [Test]
  public async Task EveryIntegrityReceptor_IsRegisteredDeferred_AtEveryStageAsync() {
    var registry = new RecordingRegistry();
    var deliveries = new IntegrityDeliveryRegistry();
    var services = new ServiceCollection().AddLogging();
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddSingleton(deliveries);
    await using var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var registrars = typeof(IntegrityCheckpointReceptorRegistrar).Assembly
      .GetTypes()
      .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IHostedService).IsAssignableFrom(t))
      .Select(t => (Type: t, Ctor: t.GetConstructors().FirstOrDefault()))
      .Where(x => x.Ctor?.GetParameters() is { Length: > 0 } ps
               && ps[0].ParameterType == typeof(IServiceProvider)
               && ps.All(pi => pi.ParameterType == typeof(IServiceProvider)
                            || pi.ParameterType == typeof(IServiceScopeFactory)
                            || (pi.ParameterType.IsGenericType
                                && pi.ParameterType.GetGenericTypeDefinition() == typeof(Microsoft.Extensions.Logging.ILogger<>))))
      .ToList();
    foreach (var (_, ctor) in registrars) {
      var args = ctor!.GetParameters()
        .Select(pi => pi.ParameterType == typeof(IServiceProvider) ? sp
                    : pi.ParameterType == typeof(IServiceScopeFactory) ? scopeFactory
                    : sp.GetRequiredService(pi.ParameterType))
        .ToArray();
      await ((IHostedService)ctor.Invoke(args)).StartAsync(CancellationToken.None);
    }

    var unwrapped = registry.Registered
      .Where(r => _integrityMessages.Contains(r.Msg)
               && !(r.Receptor.GetType().IsGenericType
                    && r.Receptor.GetType().GetGenericTypeDefinition() == typeof(IntegrityDeferringReceptor<>)))
      .Select(r => $"{r.Msg.Name} at {r.Stage}: {r.Receptor.GetType().Name}")
      .ToList();
    await Assert.That(unwrapped).IsEmpty()
      .Because("an integrity receptor registered unwrapped runs on whichever instance received the message");

    foreach (var message in _integrityMessages) {
      var stages = registry.Registered.Where(r => r.Msg == message).Select(r => r.Stage).ToList();
      await Assert.That(stages).IsEquivalentTo(_stages.ToList())
        .Because($"{message.Name} must be registered, deferred, at every stage a framework receptor needs");
      var deliveryType = Whizbang.Core.TypeNameFormatter.FormatClrTypeName(message);
      await Assert.That(deliveries.TryGetHandler(deliveryType, out _)).IsTrue()
        .Because($"the drain needs a handler for every deferred {message.Name}");
    }
  }
}
