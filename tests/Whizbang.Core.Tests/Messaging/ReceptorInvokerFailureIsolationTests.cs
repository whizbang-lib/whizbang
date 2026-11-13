// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// <see cref="IReceptorInvoker.InvokeEveryReceptorAsync"/> runs every receptor of a message at a stage even when one
/// throws, and reports each failure (#2310). <see cref="IReceptorInvoker.InvokeAsync"/> keeps stopping at the first
/// failure: at every other stage a receptor's exception is the message's outcome, and the receptors after it must not act
/// on a message that failed.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/ReceptorInvoker.cs</code-under-test>
[Category("Messaging")]
[Category("Lifecycle")]
public class ReceptorInvokerFailureIsolationTests {
  private const LifecycleStage STAGE = LifecycleStage.PostDistributeInline;

  private sealed record IsolationMessage(string Value) : IMessage;

  [Test]
  public async Task InvokeEveryReceptorAsync_FirstReceptorThrows_TheSecondStillRunsAndTheFailureIsReportedAsync() {
    var thrown = new InvalidOperationException("first receptor failed");
    var ran = new List<string>();
    var registry = new ListRegistry(_throwing("first", thrown, ran), _recording("second", ran));
    await using var provider = new ServiceCollection().BuildServiceProvider();
    var invoker = new ReceptorInvoker(registry, provider);
    var sink = new RecordingSink();
    var envelope = _envelope();

    await invoker.InvokeEveryReceptorAsync(envelope, STAGE, null, sink);

    await Assert.That(string.Join(",", ran)).IsEqualTo("first,second")
      .Because("one receptor's exception must not keep the next receptor of the same message from running");
    await Assert.That(sink.Failures.Count).IsEqualTo(1);
    await Assert.That(sink.Failures[0].ReceptorId).IsEqualTo("first")
      .Because("the failure names the receptor that threw");
    await Assert.That(sink.Failures[0].Exception).IsSameReferenceAs(thrown);
    await Assert.That(sink.Failures[0].Stage).IsEqualTo(STAGE);
    await Assert.That(sink.Failures[0].Envelope).IsSameReferenceAs(envelope);
  }

  [Test]
  public async Task InvokeAsync_FirstReceptorThrows_StopsAndThrowsAsync() {
    // The design choice locked here: only the Distribute stages of a flush isolate receptors. Everywhere else the first
    // failure is the message's outcome (a retry, a dead letter, the caller's exception), and the receptors after it do
    // not run against a message that failed.
    var ran = new List<string>();
    var registry = new ListRegistry(_throwing("first", new InvalidOperationException("first receptor failed"), ran), _recording("second", ran));
    await using var provider = new ServiceCollection().BuildServiceProvider();
    var invoker = new ReceptorInvoker(registry, provider);

    await Assert.That(async () => await invoker.InvokeAsync(_envelope(), STAGE))
      .ThrowsExactly<InvalidOperationException>();
    await Assert.That(string.Join(",", ran)).IsEqualTo("first")
      .Because("InvokeAsync stops at the first failure");
  }

  [Test]
  public async Task InvokeEveryReceptorAsync_ACanceledInvocation_IsThrownAndStopsAsync() {
    using var cts = new CancellationTokenSource();
    var ran = new List<string>();
    var registry = new ListRegistry(
      new ReceptorInfo(typeof(IsolationMessage), "first", async (_, _, _, _, ct) => {
        ran.Add("first");
        await cts.CancelAsync();
        ct.ThrowIfCancellationRequested();
        return null;
      }),
      _recording("second", ran));
    await using var provider = new ServiceCollection().BuildServiceProvider();
    var invoker = new ReceptorInvoker(registry, provider);
    var sink = new RecordingSink();

    await Assert.That(async () => await invoker.InvokeEveryReceptorAsync(_envelope(), STAGE, null, sink, cts.Token))
      .Throws<OperationCanceledException>()
      .Because("a canceled invocation ends; it is not a receptor failure to report and move past");
    await Assert.That(string.Join(",", ran)).IsEqualTo("first");
    await Assert.That(sink.Failures).IsEmpty();
  }

  [Test]
  public async Task InvokeEveryReceptorAsync_AReceptorsOwnCancellation_IsReportedAsAFailureAsync() {
    var ran = new List<string>();
    var registry = new ListRegistry(
      _throwing("first", new OperationCanceledException("the receptor's own timeout"), ran),
      _recording("second", ran));
    await using var provider = new ServiceCollection().BuildServiceProvider();
    var invoker = new ReceptorInvoker(registry, provider);
    var sink = new RecordingSink();

    await invoker.InvokeEveryReceptorAsync(_envelope(), STAGE, null, sink);

    await Assert.That(string.Join(",", ran)).IsEqualTo("first,second")
      .Because("an OperationCanceledException the invocation's own token did not cause is the receptor's failure");
    await Assert.That(sink.Failures.Count).IsEqualTo(1);
  }

  [Test]
  public async Task InvokeEveryReceptorAsync_AReceptorFailed_TagsAreNotProcessedAsync() {
    // Tags run after every receptor of the message succeeded, as they did when the first failure was thrown.
    var tags = new CountingTagProcessor();
    var registry = new ListRegistry(_throwing("first", new InvalidOperationException("failed"), []));
    await using var provider = new ServiceCollection().AddSingleton<IMessageTagProcessor>(tags).BuildServiceProvider();
    var invoker = new ReceptorInvoker(registry, provider);

    await invoker.InvokeEveryReceptorAsync(_envelope(), STAGE, null, new RecordingSink());

    await Assert.That(tags.Calls).IsEqualTo(0);
  }

  [Test]
  public async Task InvokeEveryReceptorAsync_EveryReceptorSucceeds_TagsAreProcessedAndNothingIsReportedAsync() {
    var tags = new CountingTagProcessor();
    var ran = new List<string>();
    var registry = new ListRegistry(_recording("first", ran), _recording("second", ran));
    await using var provider = new ServiceCollection().AddSingleton<IMessageTagProcessor>(tags).BuildServiceProvider();
    var invoker = new ReceptorInvoker(registry, provider);
    var sink = new RecordingSink();

    await invoker.InvokeEveryReceptorAsync(_envelope(), STAGE, null, sink);

    await Assert.That(string.Join(",", ran)).IsEqualTo("first,second");
    await Assert.That(sink.Failures).IsEmpty();
    await Assert.That(tags.Calls).IsEqualTo(1);
  }

  [Test]
  public async Task InvokeEveryReceptorAsync_DefaultImplementation_IsInvokeAsyncAsync() {
    // An invoker that does not isolate its receptors throws its first failure; the flush isolates it per message.
    IReceptorInvoker invoker = new ThrowingInvoker();
    var sink = new RecordingSink();

    await Assert.That(async () => await invoker.InvokeEveryReceptorAsync(_envelope(), STAGE, null, sink))
      .ThrowsExactly<InvalidOperationException>();
    await Assert.That(sink.Failures).IsEmpty();
  }

  // ========================================
  // Helpers
  // ========================================

  private static MessageEnvelope<IsolationMessage> _envelope() =>
    new() {
      MessageId = MessageId.New(),
      Payload = new IsolationMessage("probe"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

  private static ReceptorInfo _recording(string id, List<string> ran) =>
    new(typeof(IsolationMessage), id, (_, _, _, _, _) => {
      ran.Add(id);
      return ValueTask.FromResult<object?>(null);
    });

  private static ReceptorInfo _throwing(string id, Exception toThrow, List<string> ran) =>
    new(typeof(IsolationMessage), id, (_, _, _, _, _) => {
      ran.Add(id);
      throw toThrow;
    });

  private sealed class ListRegistry(params ReceptorInfo[] receptors) : IReceptorRegistry {
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) =>
      messageType == typeof(IsolationMessage) && stage == STAGE ? receptors : [];

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage =>
      throw new NotSupportedException();
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage =>
      throw new NotSupportedException();
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage =>
      throw new NotSupportedException();
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage =>
      throw new NotSupportedException();
  }

  private sealed class RecordingSink : IReceptorFailureSink {
    public List<(IMessageEnvelope Envelope, LifecycleStage Stage, string ReceptorId, Exception Exception)> Failures { get; } = [];

    public void OnReceptorFailed(IMessageEnvelope envelope, LifecycleStage stage, string receptorId, Exception exception) =>
      Failures.Add((envelope, stage, receptorId, exception));
  }

  private sealed class CountingTagProcessor : IMessageTagProcessor {
    public int Calls { get; private set; }

    public ValueTask ProcessTagsAsync(object message, Type messageType, LifecycleStage stage, IScopeContext? scope = null, CancellationToken ct = default) {
      Calls++;
      return ValueTask.CompletedTask;
    }
  }

  private sealed class ThrowingInvoker : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) =>
      throw new InvalidOperationException("not isolated");
  }
}
