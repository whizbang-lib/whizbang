// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using MessageId = Whizbang.Core.ValueObjects.MessageId;

namespace Whizbang.Core.Tests.Shutdown;

/// <summary>
/// A singleton the container disposes along with itself. Work that resolves it learns whether the service provider it
/// runs against is still alive: a disposed provider either refuses the resolution or hands back a disposed probe.
/// </summary>
internal sealed class ProviderLiveness : IDisposable {
  public bool Disposed { get; private set; }
  public void Dispose() => Disposed = true;

  /// <summary>True when <paramref name="services"/> still resolves a live probe.</summary>
  public static bool IsAlive(IServiceProvider services) {
    try {
      return !services.GetRequiredService<ProviderLiveness>().Disposed;
    } catch (ObjectDisposedException) {
      return false;
    }
  }
}

/// <summary>
/// One piece of background work held in flight: it signals <see cref="Entered"/>, waits for <see cref="Release"/>, then
/// records on <see cref="Finished"/> whether the service provider was still alive when it went on.
/// </summary>
internal sealed class HeldWork {
  private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

  /// <summary>Completes once the work is running and holding.</summary>
  public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

  /// <summary>Completes once the work went on after its release; true when the provider was alive then.</summary>
  public TaskCompletionSource<bool> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

  public void Release() => _release.TrySetResult();

  /// <summary>Holds until released, then checks the provider the work runs against.</summary>
  public async Task HoldAsync(IServiceProvider services) {
    Entered.TrySetResult();
    await _release.Task.ConfigureAwait(false);
    Finished.TrySetResult(ProviderLiveness.IsAlive(services));
  }
}

/// <summary>A receptor invoker that holds the given lifecycle stage, resolving the liveness probe from its own scope.</summary>
internal sealed class HeldStageInvoker(HeldWork held, LifecycleStage heldStage, IServiceProvider scope) : IReceptorInvoker {
  public async ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null,
      CancellationToken cancellationToken = default) {
    if (stage == heldStage) {
      await held.HoldAsync(scope).ConfigureAwait(false);
    }
  }
}

/// <summary>Hands the payload back as it is: the receptors in these tests never look at it.</summary>
internal sealed class PassThroughLifecycleDeserializer : ILifecycleMessageDeserializer {
  public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => envelope.Payload;
  public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => envelope.Payload;
  public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => JsonElement.Parse(jsonBytes);
  public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => jsonElement;
}

/// <summary>Stops a host the way an application shutting down does, and reports whether the stop waited.</summary>
internal static class HostShutdown {
  /// <summary>
  /// Stops and disposes <paramref name="host"/> while <paramref name="held"/> is in flight. Returns true when the stop was
  /// still waiting once <paramref name="drain"/> started waiting for in-flight work, and false when the host finished
  /// stopping, container disposed, without waiting. The held work is released either way, and this returns once the
  /// host has stopped.
  /// </summary>
  public static async Task<bool> StopWaitsForAsync(IHost host, DetachedLifecycleWork drain, HeldWork held) {
    var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    drain.Waiting += _ => waiting.TrySetResult();
    var shutdown = Task.Run(async () => {
      await host.StopAsync().ConfigureAwait(false);
      await ((IAsyncDisposable)host).DisposeAsync().ConfigureAwait(false);
    });
    try {
      await Task.WhenAny(shutdown, waiting.Task).ConfigureAwait(false);
      return !shutdown.IsCompleted;
    } finally {
      held.Release();
      await shutdown.ConfigureAwait(false);
    }
  }

  /// <summary>
  /// Runs <paramref name="stop"/> while <paramref name="held"/> is in flight and reports whether it was still running
  /// once <paramref name="drain"/> started waiting. The held work is released either way.
  /// </summary>
  public static async Task<bool> StopWaitsForAsync(Func<Task> stop, DetachedLifecycleWork drain, HeldWork held) {
    var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    drain.Waiting += _ => waiting.TrySetResult();
    var stopping = Task.Run(stop);
    try {
      await Task.WhenAny(stopping, waiting.Task).ConfigureAwait(false);
      return !stopping.IsCompleted;
    } finally {
      held.Release();
      await stopping.ConfigureAwait(false);
    }
  }

  /// <summary>An outbox message whose payload is an empty JSON object.</summary>
  public static OutboxMessage OutboxMessage(string destination = "orders") {
    var messageId = MessageId.New();
    return new OutboxMessage {
      MessageId = messageId.Value,
      Destination = destination,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = messageId,
        Payload = JsonElement.Parse("{}"),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      },
      Metadata = new EnvelopeMetadata { MessageId = messageId, Hops = [] },
      EnvelopeType = "MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
    };
  }
}
