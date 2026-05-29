using System.Threading;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Drives <see cref="NullReceptorInvoker"/> — the null-object fallback bound
/// when no source-generated receptor invoker is available (e.g., test
/// configurations or services that don't emit lifecycle receptors). Locks
/// the "no exception, returns completed ValueTask" contract that callers
/// depend on so the system stays functional in receptor-less mode.
/// </summary>
/// <docs>fundamentals/lifecycle/lifecycle-stages</docs>
public class NullReceptorInvokerTests {

  private sealed record _NoOpMessage : IMessage;

  [Test]
  public async Task InvokeAsync_WithEnvelope_CompletesSilentlyAsync() {
    var invoker = new NullReceptorInvoker();
    var envelope = new MessageEnvelope<_NoOpMessage> {
      MessageId = MessageId.New(),
      Payload = new _NoOpMessage(),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local },
    };

    await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);
    // contract: returns a completed ValueTask, throws no exception
  }

  [Test]
  public async Task InvokeAsync_WithCancellationToken_IgnoresIt_AndCompletesAsync() {
    var invoker = new NullReceptorInvoker();
    var envelope = new MessageEnvelope<_NoOpMessage> {
      MessageId = MessageId.New(),
      Payload = new _NoOpMessage(),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local },
    };
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    // Even with a pre-cancelled token, the no-op invoker doesn't observe it
    // — it never awaits anything cancellable.
    await invoker.InvokeAsync(envelope, LifecycleStage.LocalImmediateInline, cancellationToken: cts.Token);
  }

  [Test]
  public async Task InvokeAsync_ForEveryLifecycleStage_CompletesAsync() {
    var invoker = new NullReceptorInvoker();
    var envelope = new MessageEnvelope<_NoOpMessage> {
      MessageId = MessageId.New(),
      Payload = new _NoOpMessage(),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local },
    };

    foreach (var stage in Enum.GetValues<LifecycleStage>()) {
      await invoker.InvokeAsync(envelope, stage);
    }
    // Reaching here = every stage no-op'd successfully.
    await Assert.That(invoker).IsNotNull();
  }
}
