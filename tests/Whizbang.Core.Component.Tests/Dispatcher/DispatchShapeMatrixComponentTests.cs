// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Component.Tests.ShapeMatrix;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Every public entry point × every handler shape × owned or not × command or event, on the REAL
/// generated dispatcher (#1410 follow-up). Each cell has a local handler, so on every entry the handler
/// runs exactly once, a result it returns cascades and runs exactly once, and the outbox copy is written
/// exactly when the entry's policy sends the message on, marked as handled here. This is the property
/// #1410 broke: the decision said "run it here" and nothing ran.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/ReceptorDiscoveryGenerator.cs</code-under-test>
/// <docs>fundamentals/dispatcher/dispatch-policy#decision-table</docs>
public class DispatchShapeMatrixComponentTests {
  private const string OWNED_DOMAIN = "Whizbang.Core.Component.Tests.ShapeMatrix.Owned";

  private static readonly string[] _entries = [
    "SendAsync", "SendManyAsync", "LocalInvokeAsync", "LocalInvokeAsyncWithOptions", "LocalSendManyAsync",
    "PublishAsync", "PublishManyAsync", "CascadeBoth", "CascadeLocal",
  ];

  private static readonly string[] _resultShapes = ["ResultAsync", "TupleAsync", "ResultSync", "RuntimeResult"];

  public static IEnumerable<Func<(string Entry, string Kind, string Owned, string Shape)>> Cells() =>
    from entry in _entries
    from kind in ShapeCatalog.Kinds
    from owned in ShapeCatalog.Ownership
    from shape in ShapeCatalog.Shapes
    select (Func<(string, string, string, string)>)(() => (entry, kind, owned, shape));

  [Test]
  [MethodDataSource(nameof(Cells))]
  public async Task EveryEntry_RunsEveryHandlerShapeOnce_CascadesItsResultOnce_AndWritesTheCopyItsPolicyAsksForAsync(
      string entry, string kind, string owned, string shape) {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var id = Guid.CreateVersion7();
    var message = ShapeCatalog.Create(kind, owned, shape, id);
    ShapeCatalog.RegisterRuntimeHandler(provider.GetRequiredService<IReceptorRegistry>(), message);
    var cell = $"{entry} × {kind} × {owned} × {shape}";

    await _dispatchAsync(provider.GetRequiredService<IDispatcher>(), entry, message);

    await Assert.That(ShapeProbe.RunsOf(id)).IsEqualTo(1)
      .Because($"{cell}: the service has a handler for it, so it runs here exactly once");
    await Assert.That(ShapeProbe.FollowUpsOf(id)).IsEqualTo(_resultShapes.Contains(shape) ? 1 : 0)
      .Because($"{cell}: a returned result cascades and runs exactly once; a void handler returns nothing");

    var copies = outbox.QueuedOf(message.GetType()).Where(m => m.Destination is not null).ToList();
    var expectCopy = _sendsOn(entry, kind == "Evt", owned == "Own");
    await Assert.That(copies).Count().IsEqualTo(expectCopy ? 1 : 0)
      .Because($"{cell}: the copy goes out exactly when the entry's policy sends the message on");
    if (expectCopy) {
      await Assert.That(copies[0].Envelope.DispatchContext.Mode.HasFlag(DispatchModes.LocalDispatch)).IsTrue()
        .Because($"{cell}: the copy says it was handled here, so the receiving side does not run it twice");
    }
  }

  /// <summary>Whether an entry's policy sends a handled message on to other services.</summary>
  private static bool _sendsOn(string entry, bool isEvent, bool owned) => entry switch {
    "LocalInvokeAsync" or "LocalInvokeAsyncWithOptions" or "LocalSendManyAsync" or "CascadeLocal" => false,
    // An owned command never leaves its service; everything else handled here also goes on
    _ => isEvent || !owned,
  };

  private static async Task _dispatchAsync(IDispatcher dispatcher, string entry, IShapeMessage message) {
    switch (entry) {
      case "SendAsync": await dispatcher.SendAsync((object)message); break;
      case "SendManyAsync": await dispatcher.SendManyAsync(new object[] { message }); break;
      case "LocalInvokeAsync": await dispatcher.LocalInvokeAsync((object)message); break;
      case "LocalInvokeAsyncWithOptions": await dispatcher.LocalInvokeAsync((object)message, new DispatchOptions()); break;
      case "LocalSendManyAsync": await dispatcher.LocalSendManyAsync(new object[] { message }); break;
      case "PublishAsync": await dispatcher.PublishAsync((object)message); break;
      case "PublishManyAsync": await dispatcher.PublishManyAsync(new object[] { message }); break;
      case "CascadeBoth": await dispatcher.CascadeMessageAsync(message, sourceEnvelope: null, DispatchModes.Both); break;
      default: await dispatcher.CascadeMessageAsync(message, sourceEnvelope: null, DispatchModes.Local); break;
    }
  }

  private static ServiceProvider _buildProvider(IWorkCoordinatorStrategy outbox) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddScoped<IWorkCoordinatorStrategy>(_ => outbox);
    services.AddSingleton<IEnvelopeSerializer, PolicyContextKeepingEnvelopeSerializer>();
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }
}
