// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Tests.Helpers;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Every lifecycle stage of every message passes the receptor invoker, and each way it skips a stage logs the skip at
/// debug, naming the message type (#2548). The type's name is cached by the runtime behind a weak handle, so a skip that
/// read it before checking the level would allocate after every garbage collection with debug off. Each skip is
/// measured with a collection before every run against the same runs without one: with debug off the difference is
/// zero. With debug on the skip is logged by the type's name, and with no logger at all the skip still happens.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/ReceptorInvoker.cs</code-under-test>
[Category("Core")]
[NotInParallel("AllocationMeasurement")]
public class ReceptorInvokerSkipLoggingTests {
  private const string SERVICE_NAME = "svc-skip-probe";

  public enum Skip {
    SameServicePostInbox,
    OwnedDomainFilter,
    LocalDispatchPreOutbox,
    NoReceptorsRegistered,
    StageTrackerDedup,
    ReplayModeFilter,
  }

  public enum Logging {
    None,
    DebugOff,
    DebugOn,
  }

  public sealed record SkipLoggingProbeEvent([property: StreamId] Guid Id) : IEvent;

  [Test]
  [Arguments(Skip.SameServicePostInbox)]
  [Arguments(Skip.OwnedDomainFilter)]
  [Arguments(Skip.LocalDispatchPreOutbox)]
  [Arguments(Skip.NoReceptorsRegistered)]
  [Arguments(Skip.StageTrackerDedup)]
  [Arguments(Skip.ReplayModeFilter)]
  public async Task Skip_WithDebugOff_ReadsNoTypeNameAsync(Skip skip) {
    var probe = _probe(skip, Logging.DebugOff);

    var extra = WeakNameCacheProbe.ExtraBytesAfterCollections(probe.Run);
    // The owned-domain filter decides by the type's namespace, which the runtime keeps in the same weakly held cache as
    // the name: that read is the filter's, not its log's, so it is measured alone and taken off
    var filtersOwnRead = skip == Skip.OwnedDomainFilter
      ? WeakNameCacheProbe.ExtraBytesAfterCollections(() => _ = TypeNameFormatter.NamespaceOf(typeof(SkipLoggingProbeEvent)))
      : 0L;

    await Assert.That(probe.Fired).IsEqualTo(0).Because("the measured path must be the skip");
    await Assert.That(extra - filtersOwnRead).IsEqualTo(0L)
      .Because("with debug off the skip must not read the message type's name, which a collection drops");
  }

  [Test]
  [Arguments(Skip.SameServicePostInbox, "same-service PostInbox")]
  [Arguments(Skip.OwnedDomainFilter, "owned-domain namespace filter")]
  [Arguments(Skip.LocalDispatchPreOutbox, "LocalDispatch flag at PreOutbox")]
  [Arguments(Skip.NoReceptorsRegistered, "No receptors registered")]
  [Arguments(Skip.StageTrackerDedup, "LifecycleStageTracker dedup")]
  [Arguments(Skip.ReplayModeFilter, "all receptors filtered by replay mode")]
  public async Task Skip_WithDebugOn_IsLoggedByTheTypesNameAsync(Skip skip, string reason) {
    var probe = _probe(skip, Logging.DebugOn);

    probe.Run();
    probe.Run();

    await Assert.That(probe.Fired).IsEqualTo(0);
    await Assert.That(probe.Messages.Any(m => m.Contains(reason) && m.Contains(probe.TypeName))).IsTrue()
      .Because($"the {skip} skip names the message type at debug");
  }

  [Test]
  [Arguments(Skip.SameServicePostInbox)]
  [Arguments(Skip.OwnedDomainFilter)]
  [Arguments(Skip.LocalDispatchPreOutbox)]
  [Arguments(Skip.NoReceptorsRegistered)]
  [Arguments(Skip.StageTrackerDedup)]
  [Arguments(Skip.ReplayModeFilter)]
  public async Task Skip_WithNoLogger_StillSkipsAsync(Skip skip) {
    var probe = _probe(skip, Logging.None);

    probe.Run();
    probe.Run();

    await Assert.That(probe.Fired).IsEqualTo(0);
    await Assert.That(probe.Messages).IsEmpty();
  }

  private sealed class Probe {
    public required Action Run { get; init; }
    public required Func<int> FiredCount { get; init; }
    public required CapturingProvider Provider { get; init; }
    public required string TypeName { get; init; }
    public int Fired => FiredCount();
    public IReadOnlyList<string> Messages => Provider.Messages;
  }

  private static Probe _probe(Skip skip, Logging logging) {
    var fired = 0;
    var registry = new StubRegistry();
    var services = new ServiceCollection();
    var provider = new CapturingProvider();
    if (logging != Logging.None) {
      services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(builder => builder
        .AddProvider(provider)
        .SetMinimumLevel(logging == Logging.DebugOn ? LogLevel.Debug : LogLevel.Information)));
    }

    var stage = LifecycleStage.PostLifecycleDetached;
    var mode = DispatchModes.Outbox;
    ILifecycleContext? context = null;
    IReadOnlyList<MessageHop> hops = [];
    object message = new SkipLoggingProbeEvent(Guid.CreateVersion7());
    var receptor = new ReceptorInfo(message.GetType(), "skip-probe", (_, _, _, _, _) => {
      fired++;
      return ValueTask.FromResult<object?>(null);
    });

    var runtimeInstance = new object();
    switch (skip) {
      case Skip.SameServicePostInbox:
        stage = LifecycleStage.PostInboxInline;
        services.AddSingleton<IServiceInstanceProvider>(new FixedServiceInstance());
        hops = [new MessageHop {
          ServiceInstance = new ServiceInstanceInfo { ServiceName = SERVICE_NAME, InstanceId = Guid.CreateVersion7(), HostName = "h", ProcessId = 1 },
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UnixEpoch,
        }];
        registry.Add(message.GetType(), stage, receptor);
        break;
      case Skip.OwnedDomainFilter:
        // An event outside the owned domain, so the filter skips it
        stage = LifecycleStage.PreOutboxInline;
        services.AddSingleton<IOptions<RoutingOptions>>(Options.Create(new RoutingOptions().OwnDomains("Shop.Orders")));
        registry.Add(message.GetType(), stage, receptor);
        break;
      case Skip.LocalDispatchPreOutbox:
        stage = LifecycleStage.PreOutboxInline;
        mode = DispatchModes.Outbox | DispatchModes.LocalDispatch;
        registry.Add(message.GetType(), LifecycleStage.LocalImmediateInline, receptor with { RuntimeInstance = runtimeInstance });
        registry.Add(message.GetType(), stage, receptor with { RuntimeInstance = runtimeInstance });
        break;
      case Skip.NoReceptorsRegistered:
        break;
      case Skip.StageTrackerDedup:
        stage = LifecycleStage.PostInboxInline;
        services.AddSingleton(new LifecycleStageTracker());
        registry.Add(message.GetType(), stage, receptor);
        break;
      case Skip.ReplayModeFilter:
        stage = LifecycleStage.PostInboxInline;
        context = new LifecycleExecutionContext {
          CurrentStage = stage,
          MessageSource = MessageSource.Local,
          AttemptNumber = 0,
          ProcessingMode = ProcessingMode.Replay,
          IsNewEvent = false,
        };
        registry.Add(message.GetType(), stage, receptor);
        break;
    }

    var invoker = new ReceptorInvoker(registry, services.BuildServiceProvider());
    var envelope = new MessageEnvelope<object> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = [.. hops],
      DispatchContext = new MessageDispatchContext { Mode = mode, Source = MessageSource.Local },
    };
    if (skip == Skip.StageTrackerDedup) {
      // The first run claims the stage, so every measured run is the dedup skip
      WeakNameCacheProbe.RunSynchronously(invoker.InvokeAsync(envelope, stage, context));
      fired = 0;
    }
    return new Probe {
      Run = () => WeakNameCacheProbe.RunSynchronously(invoker.InvokeAsync(envelope, stage, context)),
      FiredCount = () => fired,
      Provider = provider,
      TypeName = message.GetType().Name,
    };
  }

  private sealed class FixedServiceInstance : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => SERVICE_NAME;
    public string HostName => "h";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { ServiceName = SERVICE_NAME, InstanceId = InstanceId, HostName = HostName, ProcessId = ProcessId };
  }

  private sealed class StubRegistry : IReceptorRegistry {
    private readonly Dictionary<(Type, LifecycleStage), List<ReceptorInfo>> _receptors = [];

    public void Add(Type messageType, LifecycleStage stage, ReceptorInfo receptor) {
      if (!_receptors.TryGetValue((messageType, stage), out var list)) {
        list = [];
        _receptors[(messageType, stage)] = list;
      }
      list.Add(receptor);
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage)
      => _receptors.TryGetValue((messageType, stage), out var list) ? list : [];

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  private sealed class CapturingProvider : ILoggerProvider {
    private readonly List<string> _messages = [];
    public IReadOnlyList<string> Messages { get { lock (_messages) { return [.. _messages]; } } }
    public ILogger CreateLogger(string categoryName) => new Sink(_messages);
    public void Dispose() { }

    private sealed class Sink(List<string> messages) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => true;
      public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
          Func<TState, Exception?, string> formatter) {
        lock (messages) { messages.Add(formatter(state, exception)); }
      }
    }
  }
}
