// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Component.Tests.PolicyBehavior;
using Whizbang.Core.Component.Tests.PolicyBehavior.Owned;
using Whizbang.Core.Component.Tests.RuntimeLocalReceptors;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Dispatch.Policy;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;
using Whizbang.Core.Tests.Dispatch.Policy;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Every row of the dispatch decision table, run on the REAL generated dispatcher through the public
/// call its policy belongs to, checked against what actually happened: whether the handler ran and how
/// often, what reached the outbox, where, marked how and scheduled when, and which refusal surfaced
/// (#1410 follow-up). The Unit tests check each row's decision; this checks that the executor carries it
/// out, which is the gap #1410 lived in. A row no public call can express is listed with its reason, and
/// a row that is neither run nor listed fails the build.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <docs>fundamentals/dispatcher/dispatch-policy#decision-table</docs>
public class DecisionTableExecutionComponentTests {
  private const string OWNED_DOMAIN = "Whizbang.Core.Component.Tests.PolicyBehavior.Owned";

  /// <summary>Why a row cannot be run through a public call, or null when it can.</summary>
  private static string? _unreachable(DispatchScenario row) =>
    row.Policy == DispatchPolicies.Cascade && !row.ResultRequired && (row.Scheduled || row.Behaviors != DispatchBehaviors.None)
      ? "CascadeMessageAsync takes no options, so a cascade's schedule and overrides come from no public call; the Unit table checks the decision"
      : null;

  public static IEnumerable<Func<DispatchScenario>> Rows() =>
    DispatchScenarios.All.Where(row => _unreachable(row) is null).Select(row => (Func<DispatchScenario>)(() => row));

  [Test]
  public async Task EveryRow_IsRunHereOrListedWithItsReasonAsync() {
    var unexplained = DispatchScenarios.All.Where(row => _unreachable(row) is { Length: 0 }).Select(row => row.ToString()).ToList();
    var listed = DispatchScenarios.All.Count(row => _unreachable(row) is not null);
    var run = DispatchScenarios.All.Count(row => _unreachable(row) is null);

    await Assert.That(unexplained).IsEmpty().Because("a row left out is left out for a stated reason");
    await Assert.That(run + listed).IsEqualTo(DispatchScenarios.All.Count);
    await Assert.That(run).IsGreaterThan(listed).Because("the table is checked by execution, not by exception");
  }

  [Test]
  [MethodDataSource(nameof(Rows))]
  public async Task Row_RunsWhatTheTableSaysAsync(DispatchScenario row) {
    var outbox = new PolicyRecordingOutbox();
    await using var provider = _buildProvider(row.Outbox ? outbox : null);
    var id = Guid.CreateVersion7();
    var cell = _cellFor(row, id);
    if (row.Kind == ScenarioKind.SystemCommand && row.Handler) {
      provider.GetRequiredService<IReceptorRegistry>().Register(new SystemCommandRuntimeReceptor<ClearCacheCommand>(), LifecycleStage.LocalImmediateInline);
    }

    Task dispatch() => _dispatchAsync(provider.GetRequiredService<IDispatcher>(), row, cell);

    if (_expectedException(row) is { } expected) {
      var thrown = await Assert.That(dispatch).Throws<Exception>();
      await Assert.That(thrown!.GetType()).IsEqualTo(expected).Because($"{row}: the refusal surfaces as {expected.Name}");
      await Assert.That(cell.Ran()).IsEqualTo(0).Because($"{row}: a refused dispatch runs nothing");
      await Assert.That(outbox.QueuedOf(cell.Message.GetType())).IsEmpty().Because($"{row}: a refused dispatch queues nothing");
      return;
    }

    await dispatch();

    await Assert.That(cell.Ran()).IsEqualTo(row.ExpectedLocal == LocalRun.Now ? 1 : 0)
      .Because($"{row}: the handler runs exactly when the table says it runs now");
    if (!row.Outbox) {
      return;
    }
    var queued = outbox.QueuedOf(cell.Message.GetType());
    var transported = queued.Where(m => m.Destination is not null).ToList();
    var storedOnly = queued.Where(m => m.Destination is null).ToList();
    await Assert.That(transported).Count().IsEqualTo(row.ExpectedOutbox ? 1 : 0)
      .Because($"{row}: a copy goes out exactly when the table says");
    await Assert.That(storedOnly).Count().IsEqualTo(row.ExpectedStoreOnly ? 1 : 0)
      .Because($"{row}: the event is stored without transport exactly when the table says");
    if (row.ExpectedOutbox) {
      var copy = transported[0];
      await Assert.That(copy.Envelope.DispatchContext.Mode.HasFlag(DispatchModes.LocalDispatch)).IsEqualTo(row.ExpectedMarked)
        .Because($"{row}: the copy says it was handled here exactly when it was");
      await Assert.That(copy.Envelope.DispatchContext.LocalDeferred).IsEqualTo(row.ExpectedLocal == LocalRun.AtScheduledTime)
        .Because($"{row}: the copy says this service handles it when it comes back exactly when the run was deferred");
      await Assert.That(copy.ScheduledFor).IsEqualTo(row.Scheduled ? DispatchScenario.ScheduledAt : null)
        .Because($"{row}: the copy carries the schedule");
    }
  }

  private sealed record Cell(object Message, Func<int> Ran, Type? ResultType);

  /// <summary>The message that fills a row's (kind, owned, handler) cell, and how to count its handler's runs.</summary>
  private static Cell _cellFor(DispatchScenario row, Guid id) {
    static Func<int> probe(string receptor, Guid id) => () => PolicyProbe.Count(receptor, id);
    static int never() => 0;
    return (row.Kind, row.Owned, row.Handler, row.ResultRequired) switch {
      (ScenarioKind.SystemCommand, _, true, _) => new Cell(new ClearCacheCommand(CacheKey: id.ToString()), () => RuntimeReceptorProbe.CountFor(id), null),
      (ScenarioKind.SystemCommand, _, false, _) => new Cell(new ClearCacheCommand(CacheKey: id.ToString()), never, null),
      (ScenarioKind.Command, _, true, true) => new Cell(new ScRpcCommand(id), probe(nameof(ScRpcCommandReceptor), id), typeof(ScResponse)),
      (ScenarioKind.Command, _, false, true) => new Cell(new PbUnhandledCommand(id), never, typeof(PbUnhandledResult)),
      (ScenarioKind.Event, _, true, true) => new Cell(new ScRpcEvent(id), probe(nameof(ScRpcEventReceptor), id), typeof(ScResponse)),
      (ScenarioKind.Event, _, false, true) => new Cell(new ScUnhandledEvent(id), never, typeof(ScResponse)),
      (ScenarioKind.Command, true, true, _) => new Cell(new PbOwnedCommand(id), probe(nameof(PbOwnedCommandReceptor), id), null),
      (ScenarioKind.Command, true, false, _) => new Cell(new PbOwnedUnhandledCommand(id), never, null),
      (ScenarioKind.Command, false, true, _) => new Cell(new PbUnownedCommand(id), probe(nameof(PbUnownedCommandReceptor), id), null),
      (ScenarioKind.Command, false, false, _) => new Cell(new PbUnhandledCommand(id), never, null),
      (ScenarioKind.Event, true, true, _) => new Cell(new ScOwnedEvent(id), probe(nameof(ScOwnedEventReceptor), id), null),
      (ScenarioKind.Event, true, false, _) => new Cell(new ScOwnedUnhandledEvent(id), never, null),
      (ScenarioKind.Event, false, true, _) => new Cell(new PbEvent(id), probe(nameof(PbEventReceptor), id), null),
      _ => new Cell(new ScUnhandledEvent(id), never, null),
    };
  }

  /// <summary>Runs the row through the public call its policy belongs to.</summary>
  private static async Task _dispatchAsync(IDispatcher dispatcher, DispatchScenario row, Cell cell) {
    var options = new DispatchOptions();
    if (row.Scheduled) {
      options.WithScheduledFor(DispatchScenario.ScheduledAt);
    }
    if (row.Behaviors != DispatchBehaviors.None) {
      options.WithBehaviors(row.Behaviors);
    }
    var payload = row.Route is { } route ? _routed(cell.Message, route) : cell.Message;

    if (row.ResultRequired) {
      // A caller that needs the result is a typed local invoke; a row for another policy selects it
      if (row.Policy != DispatchPolicies.LocalInvoke) {
        options.WithPolicy(row.Policy.Id);
      }
      if (cell.ResultType == typeof(PbUnhandledResult)) {
        await dispatcher.LocalInvokeAsync<PbUnhandledResult>(payload, options);
      } else {
        await dispatcher.LocalInvokeAsync<ScResponse>(payload, options);
      }
      return;
    }

    switch (row.Policy.Name) {
      case "Send": await dispatcher.SendAsync(payload, options); break;
      case "SendMany": await dispatcher.SendManyAsync(new[] { payload }, options); break;
      case "LocalInvoke": await dispatcher.LocalInvokeAsync(payload, options); break;
      case "LocalSendMany": await dispatcher.LocalSendManyAsync(new[] { payload }, options); break;
      case "Publish": await dispatcher.PublishAsync(payload, options); break;
      case "PublishMany": await dispatcher.PublishManyAsync(new[] { payload }, options); break;
      case "Cascade": await dispatcher.CascadeMessageAsync((IMessage)cell.Message, sourceEnvelope: null, row.Route ?? DispatchModes.Both); break;
      default: throw new InvalidOperationException($"No public call for policy {row.Policy.Name}");
    }
  }

  private static object _routed(object message, DispatchModes route) => route switch {
    DispatchModes.None => Route.None(),
    DispatchModes.Local => Route.Local(message),
    DispatchModes.LocalNoPersist => Route.LocalNoPersist(message),
    DispatchModes.Outbox => Route.Outbox(message),
    DispatchModes.EventStoreOnly => Route.EventStoreOnly(message),
    _ => Route.Both(message),
  };

  /// <summary>The exception a refused row surfaces as, or null when the row is not refused or refused quietly.</summary>
  private static Type? _expectedException(DispatchScenario row) => row.ExpectedReject switch {
    DispatchRejection.None => null,
    // A cascade routed nowhere has always been skipped quietly
    DispatchRejection.NoRoute when row.Policy == DispatchPolicies.Cascade && !row.ResultRequired => null,
    DispatchRejection.NoHandler or DispatchRejection.NoOutbox => typeof(ReceptorNotFoundException),
    DispatchRejection.NoRoute or DispatchRejection.ConflictingBehaviors => typeof(ArgumentException),
    _ => typeof(DispatchRejectedException),
  };

  private static ServiceProvider _buildProvider(IWorkCoordinatorStrategy? outbox) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    if (outbox is not null) {
      services.AddScoped<IWorkCoordinatorStrategy>(_ => outbox);
    }
    services.AddSingleton<IEnvelopeSerializer, PolicyContextKeepingEnvelopeSerializer>();
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }
}
