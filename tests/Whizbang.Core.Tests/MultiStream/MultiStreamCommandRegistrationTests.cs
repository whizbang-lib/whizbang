// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.MultiStream;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.MultiStream;

/// <summary>
/// Registering a multi-stream command handler (#1358): one binding per command type, which turns a stored payload back
/// into the command and calls the handler for one stream, and an intake receptor registered at the stages a receptor
/// without <c>[FireAt]</c> runs at.
/// </summary>
/// <docs>fundamentals/dispatcher/multi-stream-commands#registration</docs>
[Category("MultiStream")]
public class MultiStreamCommandRegistrationTests {
  private static readonly Guid _runId = Guid.Parse("0199c000-0000-7000-8000-000000000002");
  private static readonly Guid _stream = Guid.Parse("0199c000-0000-7000-8000-0000000000c1");

  private static ServiceProvider _provider(Action<IServiceCollection>? configure = null) {
    var services = new ServiceCollection();
    services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
    services.AddMultiStreamCommandHandler<ProbeMultiStreamCommand, ProbeMultiStreamHandler>();
    configure?.Invoke(services);
    return services.BuildServiceProvider();
  }

  private static string _payload(ProbeMultiStreamCommand command) {
    var typeInfo = (JsonTypeInfo<ProbeMultiStreamCommand>)JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(ProbeMultiStreamCommand));
    return JsonSerializer.Serialize(command, typeInfo);
  }

  [Test]
  public async Task AddMultiStreamCommandHandler_RegistersOneBindingNamedForTheCommandTypeAsync() {
    await using var provider = _provider();

    var bindings = provider.GetServices<IMultiStreamCommandBinding>().ToList();

    await Assert.That(bindings).Count().IsEqualTo(1);
    await Assert.That(bindings[0].CommandType).IsEqualTo(TypeNameFormatter.Format(typeof(ProbeMultiStreamCommand)))
      .Because("the worker finds the binding by the type name the intake stored on the run");
  }

  [Test]
  public async Task AddMultiStreamCommandHandler_CalledTwiceForTheSameHandler_RegistersOnceAsync() {
    await using var provider = _provider(s => s.AddMultiStreamCommandHandler<ProbeMultiStreamCommand, ProbeMultiStreamHandler>());

    await Assert.That(provider.GetServices<IMultiStreamCommandBinding>()).Count().IsEqualTo(1);
    await Assert.That(provider.GetServices<IHostedService>().OfType<MultiStreamCommandReceptorRegistrar>()).Count().IsEqualTo(1);
  }

  [Test]
  public async Task AddMultiStreamCommandHandler_ASecondHandlerForTheSameCommand_IsRefusedAsync() {
    var services = new ServiceCollection();
    services.AddMultiStreamCommandHandler<ProbeMultiStreamCommand, ProbeMultiStreamHandler>();

    await Assert.That(() => services.AddMultiStreamCommandHandler<ProbeMultiStreamCommand, SecondProbeMultiStreamHandler>())
      .Throws<InvalidOperationException>()
      .Because("each stream is worked once per run, so a command has exactly one handler");
  }

  [Test]
  public async Task AddMultiStreamCommandHandler_TwoCommands_RegistersABindingForEachAsync() {
    await using var provider = _provider(s => s.AddMultiStreamCommandHandler<OtherProbeMultiStreamCommand, OtherProbeMultiStreamHandler>());

    var types = provider.GetServices<IMultiStreamCommandBinding>().Select(b => b.CommandType).ToList();

    await Assert.That(types).IsEquivalentTo([
      TypeNameFormatter.Format(typeof(ProbeMultiStreamCommand)),
      TypeNameFormatter.Format(typeof(OtherProbeMultiStreamCommand))]);
  }

  [Test]
  public async Task AddMultiStreamCommandHandler_WithNoStore_LeavesTheNullDefaultForADriverToReplaceAsync() {
    await using var provider = _provider();

    await Assert.That(provider.GetRequiredService<IMultiStreamCommandStore>()).IsSameReferenceAs(NullMultiStreamCommandStore.Instance);
  }

  [Test]
  public async Task AddMultiStreamCommandHandler_WithNoServices_ThrowsAsync() {
    await Assert.That(() => MultiStreamCommandServiceCollectionExtensions.AddMultiStreamCommandHandler<ProbeMultiStreamCommand, ProbeMultiStreamHandler>(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task InvokeAsync_ReadsThePayloadBackAndCallsTheHandlerForThatStreamAsync() {
    var handler = new ProbeMultiStreamHandler();
    await using var provider = _provider(s => s.AddSingleton(handler));
    var binding = provider.GetServices<IMultiStreamCommandBinding>().Single();
    var command = new ProbeMultiStreamCommand(_runId, [_stream], "label-2");

    await using var scope = provider.CreateAsyncScope();
    await binding.InvokeAsync(scope.ServiceProvider, _payload(command), _stream, CancellationToken.None);

    await Assert.That(handler.Calls).Count().IsEqualTo(1);
    await Assert.That(handler.Calls[0].StreamId).IsEqualTo(_stream);
    await Assert.That(handler.Calls[0].Command.Label).IsEqualTo("label-2");
  }

  [Test]
  public async Task InvokeAsync_APayloadThatReadsAsNull_ThrowsAsync() {
    await using var provider = _provider();
    var binding = provider.GetServices<IMultiStreamCommandBinding>().Single();

    await using var scope = provider.CreateAsyncScope();
    await Assert.That(async () => await binding.InvokeAsync(scope.ServiceProvider, "null", _stream, CancellationToken.None))
      .Throws<InvalidOperationException>()
      .Because("a stored run whose payload is gone cannot be worked, and saying so is better than calling the handler with nothing");
  }

  [Test]
  public async Task RegisterIntake_WithNoLoggerRegistered_StillRegistersTheIntakeAsync() {
    var services = new ServiceCollection();
    services.AddMultiStreamCommandHandler<ProbeMultiStreamCommand, ProbeMultiStreamHandler>();
    await using var provider = services.BuildServiceProvider();
    var registry = new CapturingReceptorRegistry();

    provider.GetServices<IMultiStreamCommandBinding>().Single().RegisterIntake(registry, provider);

    await Assert.That(registry.Registrations.Select(r => r.Stage)).IsEquivalentTo([
      LifecycleStage.LocalImmediateInline, LifecycleStage.PreOutboxInline, LifecycleStage.PostInboxInline])
      .Because("the command is recorded whether it is dispatched within the service or arrives from the transport, with or without logging");
    await Assert.That(registry.Registrations.All(r => r.MessageType == typeof(ProbeMultiStreamCommand))).IsTrue();
    await Assert.That(registry.Receptors.All(r => r is MultiStreamCommandIntake<ProbeMultiStreamCommand>)).IsTrue();
  }

  private sealed class CapturingReceptorRegistry : IReceptorRegistry {
    public List<(Type MessageType, LifecycleStage Stage)> Registrations { get; } = [];

    public List<object> Receptors { get; } = [];

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => [];

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage {
      Registrations.Add((typeof(TMessage), stage));
      Receptors.Add(receptor);
    }

    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage =>
      throw new InvalidOperationException("The intake is a void receptor.");

    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;

    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }
}
