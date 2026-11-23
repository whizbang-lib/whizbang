// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.DerivedSurface.Owned;
using Whizbang.Core.Component.Tests.DerivedSurface.Unowned;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Every public dispatch method, called inside a receptor's handling, takes its ids from the handling (#2543): run the
/// same handling twice (a redelivery) and every id the call produces (its receipts, the outbox copies it writes, the
/// envelope its local receptor is handed) is the same; outside a handling every id is a fresh UUIDv7, as before.
/// </summary>
/// <remarks>
/// This is the design lock. <see cref="_rows"/> lists a probe for every method of <see cref="IDispatcher"/>, and
/// <see cref="_coveredElsewhere"/> the few that only delegate to a probed one; a method in neither fails
/// <see cref="EveryDispatchMethod_IsProbedOrDelegatesToAProbedOneAsync"/>, so a new dispatch method cannot ship without
/// proving it derives. The source guard (<c>DispatcherMessageIdSourceGuardTests</c>) locks the implementation side: the
/// dispatcher creates a message id in one place.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <docs>fundamentals/dispatcher/message-cascade#emission-identity</docs>
[Category("Dispatcher")]
[Category("Messaging")]
public class DispatchSurfaceDerivedIdentityComponentTests {
  private const string OWNED_DOMAIN = "Whizbang.Core.Component.Tests.DerivedSurface.Owned";
  private const string HANDLER = "DerivedSurfaceHandler";

  /// <summary>One way to call a dispatch method: the method's signature, what is special about it, and the call.</summary>
  public sealed record Row(string Signature, string Scenario, bool WithOutbox, Func<IDispatcher, Guid[], Task<IEnumerable<Guid>>> Call) {
    /// <inheritdoc />
    public override string ToString() => Scenario.Length == 0 ? Signature : $"{Signature} [{Scenario}]";
  }

  private static readonly Row[] _rows = [
    // ---- send ----
    new("SendAsync<TMessage>(TMessage)", "", true, async (d, k) => [(await d.SendAsync(new DsCommand(k[0]))).MessageId.Value]),
    new("SendAsync(Object)", "", true, async (d, k) => [(await d.SendAsync((object)new DsCommand(k[0]))).MessageId.Value]),
    new("SendAsync(Object,IMessageContext,String,String,Int32)", "", true,
      async (d, k) => [(await d.SendAsync((object)new DsCommand(k[0]), MessageContext.New())).MessageId.Value]),
    new("SendAsync<TMessage>(TMessage,DispatchOptions)", "", true,
      async (d, k) => [(await d.SendAsync(new DsCommand(k[0]), new DispatchOptions())).MessageId.Value]),
    new("SendAsync(Object,DispatchOptions)", "", true,
      async (d, k) => [(await d.SendAsync((object)new DsCommand(k[0]), new DispatchOptions())).MessageId.Value]),
    new("SendAsync(Object,IMessageContext,DispatchOptions,String,String,Int32)", "", true,
      async (d, k) => [(await d.SendAsync((object)new DsCommand(k[0]), MessageContext.New(), new DispatchOptions())).MessageId.Value]),
    new("SendAsync<TMessage>(TMessage)", "an event, stored and run here", true,
      async (d, k) => [(await d.SendAsync(Route.Both(new DsEvent(k[0])))).MessageId.Value]),

    // ---- local invoke ----
    new("LocalInvokeAsync<TMessage,TResult>(TMessage)", "", true, async (d, k) => { await d.LocalInvokeAsync<DsCommand, DsAnswer>(new DsCommand(k[0])); return []; }),
    new("LocalInvokeAsync<TResult>(Object)", "", true, async (d, k) => { await d.LocalInvokeAsync<DsAnswer>((object)new DsCommand(k[0])); return []; }),
    new("LocalInvokeAsync<TMessage,TResult>(TMessage,IMessageContext,String,String,Int32)", "", true,
      async (d, k) => { await d.LocalInvokeAsync<DsCommand, DsAnswer>(new DsCommand(k[0]), MessageContext.New()); return []; }),
    new("LocalInvokeAsync<TResult>(Object,IMessageContext,String,String,Int32)", "", true,
      async (d, k) => { await d.LocalInvokeAsync<DsAnswer>((object)new DsCommand(k[0]), MessageContext.New()); return []; }),
    new("LocalInvokeAsync<TMessage>(TMessage)", "", true, async (d, k) => { await d.LocalInvokeAsync<DsCommand>(new DsCommand(k[0])); return []; }),
    new("LocalInvokeAsync(Object)", "", true, async (d, k) => { await d.LocalInvokeAsync((object)new DsCommand(k[0])); return []; }),
    new("LocalInvokeAsync<TMessage>(TMessage,IMessageContext,String,String,Int32)", "", true,
      async (d, k) => { await d.LocalInvokeAsync<DsCommand>(new DsCommand(k[0]), MessageContext.New()); return []; }),
    new("LocalInvokeAsync(Object,IMessageContext,String,String,Int32)", "", true,
      async (d, k) => { await d.LocalInvokeAsync((object)new DsCommand(k[0]), MessageContext.New()); return []; }),
    new("LocalInvokeAsync<TResult>(Object,DispatchOptions)", "", true,
      async (d, k) => { await d.LocalInvokeAsync<DsAnswer>((object)new DsCommand(k[0]), new DispatchOptions()); return []; }),
    new("LocalInvokeAsync(Object,DispatchOptions)", "", true,
      async (d, k) => { await d.LocalInvokeAsync((object)new DsCommand(k[0]), new DispatchOptions()); return []; }),
    new("LocalInvokeWithReceiptAsync<TMessage,TResult>(TMessage)", "", true,
      async (d, k) => [(await d.LocalInvokeWithReceiptAsync<DsCommand, DsAnswer>(new DsCommand(k[0]))).Receipt.MessageId.Value]),
    new("LocalInvokeWithReceiptAsync<TResult>(Object)", "", true,
      async (d, k) => [(await d.LocalInvokeWithReceiptAsync<DsAnswer>((object)new DsCommand(k[0]))).Receipt.MessageId.Value]),
    new("LocalInvokeWithReceiptAsync<TMessage,TResult>(TMessage,IMessageContext,String,String,Int32)", "", true,
      async (d, k) => [(await d.LocalInvokeWithReceiptAsync<DsCommand, DsAnswer>(new DsCommand(k[0]), MessageContext.New())).Receipt.MessageId.Value]),
    new("LocalInvokeWithReceiptAsync<TResult>(Object,IMessageContext,String,String,Int32)", "", true,
      async (d, k) => [(await d.LocalInvokeWithReceiptAsync<DsAnswer>((object)new DsCommand(k[0]), MessageContext.New())).Receipt.MessageId.Value]),
    new("LocalInvokeWithReceiptAsync<TResult>(Object,DispatchOptions)", "", true,
      async (d, k) => [(await d.LocalInvokeWithReceiptAsync<DsAnswer>((object)new DsCommand(k[0]), new DispatchOptions())).Receipt.MessageId.Value]),

    // ---- publish ----
    new("PublishAsync<TEvent>(TEvent)", "", true, async (d, k) => [(await d.PublishAsync(new DsEvent(k[0]))).MessageId.Value]),
    new("PublishAsync<TEvent>(TEvent,DispatchOptions)", "", true,
      async (d, k) => [(await d.PublishAsync(new DsEvent(k[0]), new DispatchOptions())).MessageId.Value]),
    new("PublishAsync<TEvent>(TEvent)", "routed to local receptors only", true,
      async (d, k) => [(await d.PublishAsync(Route.Local(new DsEvent(k[0])))).MessageId.Value]),
    new("PublishAsync<TEvent>(TEvent)", "no outbox", false, async (d, k) => [(await d.PublishAsync(new DsEvent(k[0]))).MessageId.Value]),
    new("CascadeMessageAsync(IMessage,IMessageEnvelope,DispatchModes,CancellationToken)", "an event, no source envelope", true,
      async (d, k) => { await d.CascadeMessageAsync(new DsEvent(k[0]), sourceEnvelope: null, DispatchModes.Both); return []; }),
    new("CascadeMessageAsync(IMessage,IMessageEnvelope,DispatchModes,CancellationToken)", "a command, no source envelope", true,
      async (d, k) => { await d.CascadeMessageAsync(new DsCommand(k[0]), sourceEnvelope: null, DispatchModes.Both); return []; }),

    // ---- batches: one id per item, by the item's position in the handling ----
    new("SendManyAsync<TMessage>(IEnumerable<TMessage>)", "", true, async (d, k) => _ids(await d.SendManyAsync(_commands(k)))),
    new("SendManyAsync(IEnumerable<Object>)", "", true, async (d, k) => _ids(await d.SendManyAsync(_objects(_commands(k))))),
    new("SendManyAsync<TMessage>(IEnumerable<TMessage>,DispatchOptions)", "", true,
      async (d, k) => _ids(await d.SendManyAsync(_commands(k), new DispatchOptions()))),
    new("SendManyAsync(IEnumerable<Object>,DispatchOptions)", "", true,
      async (d, k) => _ids(await d.SendManyAsync(_objects(_commands(k)), new DispatchOptions()))),
    new("LocalSendManyAsync<TMessage>(IEnumerable<TMessage>)", "", true, async (d, k) => _ids(await d.LocalSendManyAsync(_commands(k)))),
    new("LocalSendManyAsync(IEnumerable<Object>)", "", true, async (d, k) => _ids(await d.LocalSendManyAsync(_objects(_commands(k))))),
    new("LocalSendManyAsync<TMessage>(IEnumerable<TMessage>,DispatchOptions)", "", true,
      async (d, k) => _ids(await d.LocalSendManyAsync(_commands(k), new DispatchOptions()))),
    new("LocalSendManyAsync(IEnumerable<Object>,DispatchOptions)", "", true,
      async (d, k) => _ids(await d.LocalSendManyAsync(_objects(_commands(k)), new DispatchOptions()))),
    new("PublishManyAsync<TEvent>(IEnumerable<TEvent>)", "", true, async (d, k) => _ids(await d.PublishManyAsync(_events(k)))),
    new("PublishManyAsync(IEnumerable<Object>)", "", true, async (d, k) => _ids(await d.PublishManyAsync(_objects(_events(k))))),
    new("PublishManyAsync<TEvent>(IEnumerable<TEvent>,DispatchOptions)", "", true,
      async (d, k) => _ids(await d.PublishManyAsync(_events(k), new DispatchOptions()))),
    new("PublishManyAsync(IEnumerable<Object>,DispatchOptions)", "", true,
      async (d, k) => _ids(await d.PublishManyAsync(_objects(_events(k)), new DispatchOptions()))),
    new("PublishManyAsync(IEnumerable<Object>)", "no outbox: the local publish path", false,
      async (d, k) => _ids(await d.PublishManyAsync(_objects(_events(k))))),
    new("LocalInvokeManyAsync<TResult>(IEnumerable<Object>)", "", true,
      async (d, k) => { await d.LocalInvokeManyAsync<DsAnswer>(_objects(_commands(k))); return []; }),
  ];

  /// <summary>Dispatch methods with no probe of their own, and the probed path each one runs through.</summary>
  private static readonly Dictionary<string, string> _coveredElsewhere = new(StringComparer.Ordinal) {
    ["PublishOnceAsync<TEvent>(String,TEvent,CancellationToken)"] = "claims the key, then publishes through PublishAsync<TEvent>(TEvent)",
    ["LocalInvokeAndSyncAsync<TMessage,TResult>(TMessage,Nullable<TimeSpan>,Action<SyncWaitingContext>,Action<SyncDecisionContext>,CancellationToken)"] =
      "invokes through the LocalInvokeAsync envelope path, then waits for perspectives; the wait creates no message",
    ["LocalInvokeAndSyncAsync<TMessage>(TMessage,Nullable<TimeSpan>,Action<SyncWaitingContext>,Action<SyncDecisionContext>,CancellationToken)"] =
      "invokes through the LocalInvokeAsync envelope path, then waits for perspectives; the wait creates no message",
    ["LocalInvokeAndSyncAsync<TMessage,TResult,TPerspective>(TMessage,Nullable<TimeSpan>,Action<SyncWaitingContext>,Action<SyncDecisionContext>,CancellationToken)"] =
      "invokes through the LocalInvokeAsync envelope path, then waits for perspectives; the wait creates no message",
    ["LocalInvokeAndSyncAsync<TMessage>(TMessage,SyncMode,CancellationToken)"] =
      "invokes through the LocalInvokeAsync envelope path, then waits for perspectives; the wait creates no message",
    ["LocalInvokeAndSyncForPerspectiveAsync<TMessage,TPerspective>(TMessage,Nullable<TimeSpan>,Action<SyncWaitingContext>,Action<SyncDecisionContext>,CancellationToken)"] =
      "invokes through the LocalInvokeAsync envelope path, then waits for perspectives; the wait creates no message",
  };

  /// <summary>The probes, one test case each.</summary>
  public static IEnumerable<Func<Row>> Rows() => _rows.Select(static row => (Func<Row>)(() => row));

  /// <summary>The probes of the batch calls that return a receipt per item, one test case each.</summary>
  public static IEnumerable<Func<Row>> BatchRows() => _rows
    .Where(static row => row.Signature.Contains("ManyAsync(", StringComparison.Ordinal) || row.Signature.Contains("ManyAsync<TMessage>(", StringComparison.Ordinal)
      || row.Signature.Contains("ManyAsync<TEvent>(", StringComparison.Ordinal))
    .Select(static row => (Func<Row>)(() => row));

  // ---- the design lock ----

  [Test]
  public async Task EveryDispatchMethod_IsProbedOrDelegatesToAProbedOneAsync() {
    var surface = typeof(IDispatcher).GetMethods(BindingFlags.Public | BindingFlags.Instance).Select(_signature).ToHashSet(StringComparer.Ordinal);
    var listed = _rows.Select(r => r.Signature).Concat(_coveredElsewhere.Keys).ToHashSet(StringComparer.Ordinal);

    await Assert.That(surface).Contains("SendAsync<TMessage>(TMessage)").Because("the scan must read the dispatcher's surface, or it checks nothing");
    await Assert.That(surface.Except(listed).Order(StringComparer.Ordinal).ToList()).IsEmpty()
      .Because("a new dispatch method must take its ids from the handling: add a probe row here (#2543)");
    await Assert.That(listed.Except(surface).Order(StringComparer.Ordinal).ToList()).IsEmpty()
      .Because("a row for a method the dispatcher no longer has proves nothing");
  }

  [Test]
  [MethodDataSource(nameof(Rows))]
  public async Task InsideAHandling_ARedeliveryProducesTheSameIdsAsync(Row row) {
    var source = (Guid)TrackedGuid.New();

    var firstRun = await _runAsync(row, _handling(source));
    var redelivery = await _runAsync(row, _handling(source));

    await Assert.That(firstRun).IsNotEmpty().Because($"{row} must produce an id to compare, or the test proves nothing");
    await Assert.That(redelivery).IsEquivalentTo(firstRun, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because($"{row}: a redelivered handling must dispatch under the ids of its first run");
  }

  [Test]
  [MethodDataSource(nameof(Rows))]
  public async Task OutsideAHandling_EveryIdIsMintedAsync(Row row) {
    var first = await _runAsync(row, handling: null);
    var second = await _runAsync(row, handling: null);

    await Assert.That(first).IsNotEmpty();
    await Assert.That(first.Concat(second).All(static id => id.Version == 7)).IsTrue()
      .Because($"{row}: with no handling an id is a fresh UUIDv7, as before");
    await Assert.That(first.Intersect(second).ToList()).IsEmpty()
      .Because($"{row}: with no handling there is nothing to derive from, so two calls share no id");
  }

  [Test]
  [MethodDataSource(nameof(Rows))]
  public async Task InsideAHandling_TwoHandlingsOfTheSameCall_ShareNoIdAsync(Row row) {
    var first = await _runAsync(row, _handling((Guid)TrackedGuid.New()));
    var second = await _runAsync(row, _handling((Guid)TrackedGuid.New()));

    await Assert.That(first.Intersect(second).ToList()).IsEmpty()
      .Because($"{row}: the ids derive from the handled message, so another handling is another set of ids");
  }

  [Test]
  [MethodDataSource(nameof(BatchRows))]
  public async Task InsideAHandling_ABatchGivesEachItemAnIdOfItsOwnAsync(Row row) {
    var dispatcher = _build(row.WithOutbox, out _);
    Guid[] keys = [(Guid)TrackedGuid.New(), (Guid)TrackedGuid.New(), (Guid)TrackedGuid.New()];

    IEnumerable<Guid> receipts;
    using (ReceptorHandling.Enter(_handling((Guid)TrackedGuid.New()))) {
      receipts = await row.Call(dispatcher, keys);
    }

    await Assert.That(receipts.Distinct().Count()).IsEqualTo(3)
      .Because($"{row}: three items are three messages, each at its own position in the handling");
  }

  [Test]
  public async Task CascadeMessageAsync_ACommandWithASourceEnvelope_StoresItsCopyUnderAnIdDerivedFromThatSourceAsync() {
    var dispatcher = _build(withOutbox: true, out var outbox, out var serviceName);
    var source = (Guid)TrackedGuid.New();

    await dispatcher.CascadeMessageAsync(new DsCascadedCommand((Guid)TrackedGuid.New()), _handling(source), DispatchModes.Outbox);

    var stored = outbox!.QueuedOf(typeof(DsCascadedCommand)).Select(static m => m.MessageId).ToList();
    await Assert.That(stored).IsEquivalentTo([SentMessageIdentity.Derive(source, serviceName, HANDLER, TypeNameFormatter.Format(typeof(DsCascadedCommand)), 0)])
      .Because("a cascaded command derives its id from the envelope it was cascaded from, even with no receptor body running");
  }

  // ---- helpers ----

  private static async Task<List<Guid>> _runAsync(Row row, IMessageEnvelope? handling) {
    var dispatcher = _build(row.WithOutbox, out var outbox);
    Guid[] keys = [(Guid)TrackedGuid.New(), (Guid)TrackedGuid.New(), (Guid)TrackedGuid.New()];
    IEnumerable<Guid> returned;
    if (handling is null) {
      returned = await row.Call(dispatcher, keys);
    } else {
      using (ReceptorHandling.Enter(handling)) {
        returned = await row.Call(dispatcher, keys);
      }
    }
    var ids = new List<Guid>(returned);
    if (outbox is not null) {
      ids.AddRange(outbox.QueuedOf(typeof(DsCommand)).Select(static m => m.MessageId));
      ids.AddRange(outbox.QueuedOf(typeof(DsEvent)).Select(static m => m.MessageId));
    }
    foreach (var key in keys) {
      ids.AddRange(DerivedSurfaceProbe.Seen(key));
    }
    return ids;
  }

  private static IDispatcher _build(bool withOutbox, out PolicyRecordingOutbox? outbox) => _build(withOutbox, out outbox, out _);

  private static IDispatcher _build(bool withOutbox, out PolicyRecordingOutbox? outbox, out string serviceName) {
    outbox = withOutbox ? new PolicyRecordingOutbox() : null;
    var instance = new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build());
    serviceName = instance.ServiceName;
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IServiceInstanceProvider>(instance);
    if (outbox is not null) {
      var strategy = outbox;
      services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    }
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider().GetRequiredService<IDispatcher>();
  }

  /// <summary>The envelope a worker hands a receptor for one handler row; a redelivery builds a new one with the same values.</summary>
  private static MessageEnvelope<object> _handling(Guid sourceMessageId) => new() {
    MessageId = MessageId.From(sourceMessageId),
    Payload = new DsAnswer(sourceMessageId),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local, HandlerName = HANDLER },
  };

  private static DsCommand[] _commands(Guid[] keys) => [.. keys.Select(static k => new DsCommand(k))];

  private static DsEvent[] _events(Guid[] keys) => [.. keys.Select(static k => new DsEvent(k))];

  private static object[] _objects<T>(T[] items) where T : notnull => [.. items.Cast<object>()];

  private static IEnumerable<Guid> _ids(IEnumerable<IDeliveryReceipt> receipts) => [.. receipts.Select(static r => r.MessageId.Value)];

  private static string _signature(MethodInfo method) {
    var generic = method.IsGenericMethodDefinition ? $"<{string.Join(",", method.GetGenericArguments().Select(static a => a.Name))}>" : "";
    return $"{method.Name}{generic}({string.Join(",", method.GetParameters().Select(static p => _typeName(p.ParameterType)))})";
  }

  private static string _typeName(Type type) {
    if (!type.IsGenericType) {
      return type.Name;
    }
    var name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
    return $"{name}<{string.Join(",", type.GetGenericArguments().Select(_typeName))}>";
  }
}
