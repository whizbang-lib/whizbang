// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Branch coverage for <see cref="ReceptorInvoker"/>: the sampled-activity side of every receptor
/// span tag (success and failure), a source hop whose service name is null, the perspective-scoped
/// exemption from the double-fire guardrail driven by the lifecycle context, and an owned domain
/// configured with a trailing separator.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/ReceptorInvoker.cs</code-under-test>
[Category("Core")]
public class ReceptorInvokerBranchCoverageTests {
  private sealed record ProbeMessage(string Value) : IMessage;
  private sealed record ProbeEvent(string Value) : IEvent;
  private sealed record ProbePerspective;

  private static MessageEnvelope<T> _envelope<T>(T payload, List<MessageHop>? hops = null, DispatchModes mode = DispatchModes.Local) => new() {
    MessageId = MessageId.From(TrackedGuid.New()),
    Payload = payload,
    Hops = hops ?? [],
    DispatchContext = new MessageDispatchContext { Mode = mode, Source = MessageSource.Local }
  };

  private static ActivityListener _listen(List<Activity> stopped) {
    var listener = new ActivityListener {
      ShouldListenTo = source => source.Name == WhizbangActivitySource.Tracing.Name,
      Sample = (ref options) => ActivitySamplingResult.AllData,
      ActivityStopped = activity => {
        lock (stopped) {
          stopped.Add(activity);
        }
      }
    };
    ActivitySource.AddActivityListener(listener);
    return listener;
  }

  private static Activity _single(List<Activity> stopped, string receptorId) {
    lock (stopped) {
      return stopped.Single(a => a.OperationName == $"Receptor {receptorId}");
    }
  }

  // ---- receptor activity, sampled -------------------------------------------------------------

  [Test]
  [NotInParallel("ActivityListener")]
  public async Task InvokeAsync_SampledActivity_SuccessfulReceptor_TagsTheSpanAndMarksItOkAsync() {
    var stopped = new List<Activity>();
    using var listener = _listen(stopped);
    var receptorId = $"ProbeReceptor-{TrackedGuid.New().Value:N}";
    var registry = new RecordingReceptorRegistry();
    registry.Register<ProbeMessage>(receptorId, LifecycleStage.LocalImmediateInline,
      static (_, _, _, _, _) => ValueTask.FromResult<object?>("a result"));
    await using var provider = new ServiceCollection().BuildServiceProvider();
    var invoker = new ReceptorInvoker(registry, provider);

    await invoker.InvokeAsync(_envelope(new ProbeMessage("x")), LifecycleStage.LocalImmediateInline);

    var activity = _single(stopped, receptorId);
    await Assert.That(activity.GetTagItem("whizbang.receptor.id")).IsEqualTo(receptorId);
    await Assert.That(activity.GetTagItem("whizbang.receptor.message_type"))
      .IsEqualTo(TypeNameFormatter.DisplayName(typeof(ProbeMessage)));
    await Assert.That(activity.GetTagItem("whizbang.lifecycle.stage")).IsEqualTo(nameof(LifecycleStage.LocalImmediateInline));
    await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Ok);
    await Assert.That(activity.GetTagItem("whizbang.receptor.has_result")).IsEqualTo(true)
      .Because("the receptor returned a value, and the span records that it did");
  }

  [Test]
  [NotInParallel("ActivityListener")]
  public async Task InvokeAsync_SampledActivity_ThrowingReceptor_MarksTheSpanErrorWithExceptionTagsAsync() {
    var stopped = new List<Activity>();
    using var listener = _listen(stopped);
    var receptorId = $"ThrowingReceptor-{TrackedGuid.New().Value:N}";
    var registry = new RecordingReceptorRegistry();
    registry.Register<ProbeMessage>(receptorId, LifecycleStage.LocalImmediateInline,
      static (_, _, _, _, _) => throw new InvalidOperationException("receptor failed"));
    await using var provider = new ServiceCollection().BuildServiceProvider();
    var invoker = new ReceptorInvoker(registry, provider);

    await Assert.That(async () => await invoker.InvokeAsync(_envelope(new ProbeMessage("x")), LifecycleStage.LocalImmediateInline))
      .Throws<InvalidOperationException>();

    var activity = _single(stopped, receptorId);
    await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Error);
    await Assert.That(activity.StatusDescription).IsEqualTo("receptor failed");
    await Assert.That(activity.GetTagItem("exception.type"))
      .IsEqualTo(TypeNameFormatter.DisplayName(typeof(InvalidOperationException)));
    await Assert.That(activity.GetTagItem("exception.message")).IsEqualTo("receptor failed");
    await Assert.That(activity.GetTagItem("whizbang.receptor.has_result")).IsNull()
      .Because("the receptor never returned, so the success-only result tag is absent");
  }

  // ---- source hop with a null service name ----------------------------------------------------

  [Test]
  public async Task InvokeAsync_LastHopHasNullServiceName_LogsAnEmptySourceServiceAsync() {
    var registry = new RecordingReceptorRegistry();
    registry.Register<ProbeMessage>("NullServiceReceptor", LifecycleStage.LocalImmediateInline,
      static (_, _, _, _, _) => ValueTask.FromResult<object?>(null));
    var services = new ServiceCollection();
    services.AddLogging(b => {
      b.SetMinimumLevel(LogLevel.Trace);
      b.AddFakeLogging();
    });
    await using var provider = services.BuildServiceProvider();
    var collector = provider.GetFakeLogCollector();
    var invoker = new ReceptorInvoker(registry, provider);
    var hops = new List<MessageHop> {
      new() {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = new ServiceInstanceInfo {
          ServiceName = null!,
          InstanceId = TrackedGuid.New(),
          HostName = "test-host",
          ProcessId = 1
        }
      }
    };

    await invoker.InvokeAsync(_envelope(new ProbeMessage("x"), hops), LifecycleStage.LocalImmediateInline);

    var firing = collector.GetSnapshot().Single(r => r.Id.Id == 16);
    var state = firing.StructuredState!.ToDictionary(p => p.Key, p => p.Value);
    await Assert.That(state["SourceService"]).IsEqualTo(string.Empty)
      .Because("a hop without a service name reports an empty source service rather than a null");
  }

  // ---- perspective-scoped exemption from the double-fire guardrail ----------------------------

  [Test]
  public async Task InvokeAsync_PriorInvocationButContextNamesAPerspective_StillFiresAsync() {
    var fired = 0;
    var registry = new RecordingReceptorRegistry();
    registry.Register<ProbeMessage>("PerPerspectiveReceptor", LifecycleStage.PostLifecycleInline,
      (_, _, _, _, _) => {
        Interlocked.Increment(ref fired);
        return ValueTask.FromResult<object?>(null);
      });
    await using var provider = new ServiceCollection()
      .AddSingleton<IReceptorDedupStore>(new AlwaysPriorDedupStore())
      .BuildServiceProvider();
    var invoker = new ReceptorInvoker(registry, provider);
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostLifecycleInline,
      PerspectiveType = typeof(ProbePerspective),
    };

    await invoker.InvokeAsync(_envelope(new ProbeMessage("x")), LifecycleStage.PostLifecycleInline, context);

    await Assert.That(fired).IsEqualTo(1)
      .Because("a lifecycle context naming a perspective makes the invocation perspective-scoped, which the guardrail exempts");
  }

  [Test]
  public async Task InvokeAsync_PriorInvocationAndContextWithoutPerspective_IsSkippedAsync() {
    // Control for the test above: the same prior record, the same stage, and no perspective on the
    // context. The guardrail must skip, which proves the perspective type is what exempted it.
    var fired = 0;
    var registry = new RecordingReceptorRegistry();
    registry.Register<ProbeMessage>("PerPerspectiveReceptor", LifecycleStage.PostLifecycleInline,
      (_, _, _, _, _) => {
        Interlocked.Increment(ref fired);
        return ValueTask.FromResult<object?>(null);
      });
    await using var provider = new ServiceCollection()
      .AddSingleton<IReceptorDedupStore>(new AlwaysPriorDedupStore())
      .BuildServiceProvider();
    var invoker = new ReceptorInvoker(registry, provider);
    var context = new LifecycleExecutionContext { CurrentStage = LifecycleStage.PostLifecycleInline };

    await invoker.InvokeAsync(_envelope(new ProbeMessage("x")), LifecycleStage.PostLifecycleInline, context);

    await Assert.That(fired).IsEqualTo(0);
  }

  // ---- owned domain with a trailing separator -------------------------------------------------

  [Test]
  public async Task InvokeAsync_PreOutbox_OwnedDomainEndingInSeparator_TreatsChildNamespaceAsOwnedAsync() {
    // The owned domain already ends in '.', so it is used as the prefix as-is. An owned event at
    // PreOutbox fires; had the prefix gained a second '.', the event would read as foreign and be
    // skipped.
    var fired = 0;
    var registry = new RecordingReceptorRegistry();
    registry.Register<ProbeEvent>("OwnedEventReceptor", LifecycleStage.PreOutboxInline,
      (_, _, _, _, _) => {
        Interlocked.Increment(ref fired);
        return ValueTask.FromResult<object?>(null);
      });
    await using var provider = new ServiceCollection()
      .AddSingleton<IOptions<RoutingOptions>>(Options.Create(new RoutingOptions().OwnDomains("Whizbang.Core.Tests.")))
      .BuildServiceProvider();
    var invoker = new ReceptorInvoker(registry, provider);

    await invoker.InvokeAsync(_envelope(new ProbeEvent("x"), mode: DispatchModes.Outbox), LifecycleStage.PreOutboxInline);

    await Assert.That(fired).IsEqualTo(1)
      .Because("the event's namespace sits under the owned domain, so it is this service's own event to publish");
  }

  // ---- fakes ----------------------------------------------------------------------------------

  /// <summary>A dedup store that always reports a prior invocation.</summary>
  private sealed class AlwaysPriorDedupStore : IReceptorDedupStore {
    private static ReceptorInvocationRecord _prior(string receptorId) => new() {
      ReceptorId = receptorId,
      Stage = LifecycleStage.LocalImmediateInline,
      CompletedAt = DateTimeOffset.UtcNow,
      Duration = TimeSpan.Zero,
      ServiceName = string.Empty
    };

    public ValueTask<ReceptorInvocationRecord?> TryGetPriorInvocationAsync(
        IMessageEnvelope envelope, string receptorId, CancellationToken cancellationToken) =>
      ValueTask.FromResult<ReceptorInvocationRecord?>(_prior(receptorId));

    public ValueTask<ReceptorInvocationRecord?> TryGetPriorInvocationAsync(
        IMessageEnvelope envelope, string receptorId, string? serviceName, CancellationToken cancellationToken) =>
      ValueTask.FromResult<ReceptorInvocationRecord?>(_prior(receptorId));

    public ValueTask RecordInvocationAsync(
        IMessageEnvelope envelope, ReceptorInvocationRecord record, CancellationToken cancellationToken) =>
      ValueTask.CompletedTask;
  }

  private sealed class RecordingReceptorRegistry : IReceptorRegistry {
    private readonly Dictionary<(Type, LifecycleStage), List<ReceptorInfo>> _receptors = [];

    public void Register<TMessage>(
        string receptorId,
        LifecycleStage stage,
        Func<IServiceProvider, object, IMessageEnvelope, ICallerInfo?, CancellationToken, ValueTask<object?>> invoke) {
      var key = (typeof(TMessage), stage);
      if (!_receptors.TryGetValue(key, out var list)) {
        list = [];
        _receptors[key] = list;
      }
      list.Add(new ReceptorInfo(MessageType: typeof(TMessage), ReceptorId: receptorId, InvokeAsync: invoke));
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) =>
      _receptors.TryGetValue((messageType, stage), out var list) ? list : [];

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
  }
}
