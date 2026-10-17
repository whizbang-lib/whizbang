// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Dispatch;
using Whizbang.Core.Dispatch.Policy;

namespace Whizbang.Core.Tests.Dispatch.Policy;

/// <summary>The kind of message a scenario dispatches.</summary>
public enum ScenarioKind {
  Command,
  SystemCommand,
  Event,
}

/// <summary>
/// One row of the dispatch decision table: a situation and the decision the owner fixed for it. The
/// same rows drive the Unit, Component and Integration tests and the docs site table, so a row means
/// the same thing at every level.
/// </summary>
/// <param name="Row">The row number in the published table.</param>
/// <param name="Name">What the row shows, in a few words.</param>
/// <param name="Policy">The policy the call uses.</param>
/// <param name="Kind">The message kind.</param>
/// <param name="Owned">True when the message type is in an owned namespace.</param>
/// <param name="Handler">True when this service has a local handler.</param>
/// <param name="Route">The route the caller chose, or null.</param>
/// <param name="Scheduled">True when the caller scheduled the message for later.</param>
/// <param name="Outbox">True when this service has an outbox.</param>
/// <param name="ExpectedLocal">When the local handlers run.</param>
/// <param name="ExpectedOutbox">True when the outbox is written.</param>
/// <param name="ExpectedStoreOnly">True when the event is stored without being sent.</param>
/// <param name="ExpectedReject">Why the dispatch is refused, or none.</param>
public sealed record DispatchScenario(
  int Row,
  string Name,
  DispatchPolicy Policy,
  ScenarioKind Kind,
  bool Owned,
  bool Handler,
  DispatchModes? Route,
  bool Scheduled,
  bool Outbox,
  LocalRun ExpectedLocal,
  bool ExpectedOutbox,
  bool ExpectedStoreOnly,
  DispatchRejection ExpectedReject) {

  /// <summary>The fixed scheduled time scenarios use.</summary>
  public static readonly DateTimeOffset ScheduledAt = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

  /// <summary>True when the expected outbox copy is marked as already handled here.</summary>
  public bool ExpectedMarked => ExpectedLocal == LocalRun.Now && ExpectedOutbox;

  /// <summary>The situation this row describes.</summary>
  public DispatchSituation Situation => ToSituation(Kind, Owned, Handler, Route, Scheduled, Outbox);

  /// <summary>The decision this row expects.</summary>
  public DispatchDecision ExpectedDecision {
    get {
      if (ExpectedReject != DispatchRejection.None) {
        return DispatchDecision.Rejected(ExpectedReject, default);
      }
      DateTimeOffset? deferUntil = Scheduled && ExpectedOutbox ? ScheduledAt : null;
      return new DispatchDecision(ExpectedLocal, ExpectedOutbox, ExpectedStoreOnly, deferUntil, DispatchRejection.None, default);
    }
  }

  /// <summary>Builds the situation for a combination of inputs.</summary>
  public static DispatchSituation ToSituation(ScenarioKind kind, bool owned, bool handler, DispatchModes? route, bool scheduled, bool outbox) {
    var traits = kind switch {
      ScenarioKind.Command => MessageTraits.Command,
      ScenarioKind.SystemCommand => MessageTraits.Command | MessageTraits.SystemCommand,
      _ => MessageTraits.Event,
    };
    if (owned) {
      traits |= MessageTraits.Owned;
    }
    return new DispatchSituation(
      typeof(DispatchScenario),
      traits,
      handler ? LocalHandlers.Generated : LocalHandlers.None,
      route,
      scheduled ? ScheduledAt : null,
      IsScheduledInFuture: scheduled,
      OutboxAvailable: outbox);
  }

  /// <inheritdoc />
  public override string ToString() => $"{Policy.Name}_Row{Row:D2}_{Name}";
}

/// <summary>The decision table, one entry per named row.</summary>
public static class DispatchScenarios {
  private const LocalRun NONE = LocalRun.None;
  private const LocalRun NOW = LocalRun.Now;
  private const LocalRun LATER = LocalRun.AtScheduledTime;
  private const DispatchRejection OK = DispatchRejection.None;

  private static DispatchScenario _row(int row, string name, DispatchPolicy policy, ScenarioKind kind,
      bool owned, bool handler, LocalRun local, bool outbox, DispatchRejection reject = OK,
      DispatchModes? route = null, bool scheduled = false, bool hasOutbox = true, bool storeOnly = false) =>
    new(row, name, policy, kind, owned, handler, route, scheduled, hasOutbox, local, outbox, storeOnly, reject);

  /// <summary>Every named row.</summary>
  public static IReadOnlyList<DispatchScenario> All { get; } = _build();

  private static List<DispatchScenario> _build() {
    var send = DispatchPolicies.Send;
    var sendMany = DispatchPolicies.SendMany;
    var local = DispatchPolicies.LocalInvoke;
    var localMany = DispatchPolicies.LocalSendMany;
    var publish = DispatchPolicies.Publish;
    var publishMany = DispatchPolicies.PublishMany;
    var cascade = DispatchPolicies.Cascade;
    const ScenarioKind C = ScenarioKind.Command;
    const ScenarioKind S = ScenarioKind.SystemCommand;
    const ScenarioKind E = ScenarioKind.Event;

    return [
      // Send
      _row(1, "UnownedCommand_Handled_RunsHereAndGoesOut", send, C, owned: false, handler: true, NOW, outbox: true),
      _row(2, "UnownedCommand_NoHandler_GoesOut", send, C, owned: false, handler: false, NONE, outbox: true),
      _row(3, "OwnedCommand_Handled_StaysHere", send, C, owned: true, handler: true, NOW, outbox: false),
      _row(4, "OwnedCommand_NoHandler_Throws", send, C, owned: true, handler: false, NONE, outbox: false, DispatchRejection.NoHandler),
      _row(5, "SystemCommand_Handled_RunsHereAndGoesOut", send, S, owned: false, handler: true, NOW, outbox: true),
      _row(6, "SystemCommand_NoHandler_GoesOut", send, S, owned: false, handler: false, NONE, outbox: true),
      _row(7, "UnownedEvent_Handled_RunsHereAndGoesOut", send, E, owned: false, handler: true, NOW, outbox: true),
      _row(8, "OwnedEvent_NoHandler_GoesOut", send, E, owned: true, handler: false, NONE, outbox: true),
      _row(9, "RoutedLocal_RunsHereOnly", send, C, owned: false, handler: true, NOW, outbox: false, route: DispatchModes.Local),
      _row(10, "RoutedOutbox_GoesOutOnly", send, C, owned: false, handler: true, NONE, outbox: true, route: DispatchModes.Outbox),
      _row(11, "OwnedCommand_RoutedOutbox_StaysHere", send, C, owned: true, handler: true, NOW, outbox: false, route: DispatchModes.Outbox),
      _row(12, "Command_RoutedEventStoreOnly_Throws", send, C, owned: false, handler: true, NONE, outbox: false, DispatchRejection.CommandCannotBeStoreOnly, route: DispatchModes.EventStoreOnly),
      _row(13, "Handled_Scheduled_RunsWhenItComesBack", send, C, owned: false, handler: true, LATER, outbox: true, scheduled: true),
      _row(14, "NoHandler_Scheduled_GoesOutLater", send, C, owned: false, handler: false, NONE, outbox: true, scheduled: true),
      _row(15, "OwnedCommand_Scheduled_Throws", send, C, owned: true, handler: true, NONE, outbox: false, DispatchRejection.CannotScheduleInProcess, scheduled: true),
      _row(16, "Handled_NoOutbox_RunsHereOnly", send, C, owned: false, handler: true, NOW, outbox: false, hasOutbox: false),
      _row(17, "NoHandler_NoOutbox_Throws", send, C, owned: false, handler: false, NONE, outbox: false, DispatchRejection.NoOutbox, hasOutbox: false),
      _row(18, "RoutedNone_Throws", send, C, owned: false, handler: true, NONE, outbox: false, DispatchRejection.NoRoute, route: DispatchModes.None),
      _row(19, "Event_RoutedEventStoreOnly_StoredOnly", send, E, owned: false, handler: true, NONE, outbox: false, route: DispatchModes.EventStoreOnly, storeOnly: true),

      // SendMany (Send per message)
      _row(20, "UnownedCommand_Handled_RunsHereAndGoesOut", sendMany, C, owned: false, handler: true, NOW, outbox: true),
      _row(21, "OwnedCommand_NoHandler_Throws", sendMany, C, owned: true, handler: false, NONE, outbox: false, DispatchRejection.NoHandler),
      _row(22, "NoHandler_NoOutbox_Throws", sendMany, C, owned: false, handler: false, NONE, outbox: false, DispatchRejection.NoOutbox, hasOutbox: false),

      // LocalInvoke / LocalSendMany
      _row(23, "Handled_RunsHereOnly", local, C, owned: false, handler: true, NOW, outbox: false),
      _row(24, "NoHandler_Throws", local, C, owned: false, handler: false, NONE, outbox: false, DispatchRejection.NoHandler),
      _row(25, "Scheduled_Throws", local, C, owned: false, handler: true, NONE, outbox: false, DispatchRejection.CannotScheduleInProcess, scheduled: true),
      _row(26, "SystemCommand_StaysHere", local, S, owned: false, handler: true, NOW, outbox: false),
      _row(27, "RouteIgnored_RunsHereOnly", local, E, owned: false, handler: true, NOW, outbox: false, route: DispatchModes.Outbox),
      _row(28, "Handled_RunsHereOnly", localMany, C, owned: false, handler: true, NOW, outbox: false),
      _row(29, "NoHandler_Throws", localMany, C, owned: false, handler: false, NONE, outbox: false, DispatchRejection.NoHandler),

      // Publish / PublishMany
      _row(30, "Event_Handled_RunsHereAndGoesOut", publish, E, owned: false, handler: true, NOW, outbox: true),
      _row(31, "Event_NoHandler_GoesOut", publish, E, owned: false, handler: false, NONE, outbox: true),
      _row(32, "Event_Handled_Scheduled_RunsWhenItComesBack", publish, E, owned: false, handler: true, LATER, outbox: true, scheduled: true),
      _row(33, "Event_NoHandler_NoOutbox_DefersOrLogs", publish, E, owned: false, handler: false, NONE, outbox: true, hasOutbox: false),
      _row(34, "Event_Handled_NoOutbox_RunsHereOnly", publish, E, owned: false, handler: true, NOW, outbox: false, hasOutbox: false),
      _row(35, "OwnedCommand_Handled_StaysHere", publish, C, owned: true, handler: true, NOW, outbox: false),
      _row(36, "OwnedCommand_NoHandler_Throws", publish, C, owned: true, handler: false, NONE, outbox: false, DispatchRejection.NoHandler),
      _row(37, "OwnedEvent_Handled_RunsHereAndGoesOut", publish, E, owned: true, handler: true, NOW, outbox: true),
      _row(38, "Event_Handled_RunsHereAndGoesOut", publishMany, E, owned: false, handler: true, NOW, outbox: true),
      _row(39, "Event_NoHandler_GoesOut", publishMany, E, owned: false, handler: false, NONE, outbox: true),

      // Cascade
      _row(40, "Event_NoRouteChosen_RunsHereAndGoesOut", cascade, E, owned: false, handler: true, NOW, outbox: true),
      _row(41, "Event_RoutedBoth_RunsHereAndGoesOut", cascade, E, owned: false, handler: true, NOW, outbox: true, route: DispatchModes.Both),
      _row(42, "OwnedCommand_RoutedBoth_StaysHere", cascade, C, owned: true, handler: true, NOW, outbox: false, route: DispatchModes.Both),
      _row(43, "Event_RoutedLocal_RunsHereAndIsStored", cascade, E, owned: false, handler: true, NOW, outbox: false, route: DispatchModes.Local, storeOnly: true),
      _row(44, "Command_RoutedLocal_RunsHereOnly", cascade, C, owned: false, handler: true, NOW, outbox: false, route: DispatchModes.Local),
      _row(45, "Event_RoutedLocalNoPersist_RunsHereOnly", cascade, E, owned: false, handler: true, NOW, outbox: false, route: DispatchModes.LocalNoPersist),
      _row(46, "Event_RoutedOutbox_GoesOutOnly", cascade, E, owned: false, handler: true, NONE, outbox: true, route: DispatchModes.Outbox),
      _row(47, "OwnedCommand_RoutedOutbox_StaysHere", cascade, C, owned: true, handler: true, NOW, outbox: false, route: DispatchModes.Outbox),
      _row(48, "Event_RoutedEventStoreOnly_StoredOnly", cascade, E, owned: false, handler: false, NONE, outbox: false, route: DispatchModes.EventStoreOnly, storeOnly: true),
      _row(49, "Command_RoutedEventStoreOnly_Throws", cascade, C, owned: false, handler: false, NONE, outbox: false, DispatchRejection.CommandCannotBeStoreOnly, route: DispatchModes.EventStoreOnly),
      _row(50, "OwnedCommand_NoHandler_Throws", cascade, C, owned: true, handler: false, NONE, outbox: false, DispatchRejection.NoHandler, route: DispatchModes.Both),
      _row(51, "SystemCommand_RoutedLocal_StillGoesOut", cascade, S, owned: false, handler: true, NOW, outbox: true, route: DispatchModes.Local),
      _row(52, "Event_RoutedLocal_Scheduled_Throws", cascade, E, owned: false, handler: true, NONE, outbox: false, DispatchRejection.CannotScheduleInProcess, route: DispatchModes.Local, scheduled: true),
      _row(53, "Event_NoHandler_NoOutbox_DefersOrLogs", cascade, E, owned: false, handler: false, NONE, outbox: true, route: DispatchModes.Both, hasOutbox: false),
    ];
  }

  /// <summary>Every value of each input axis, so every combination can be checked.</summary>
  public static IEnumerable<(DispatchPolicy Policy, ScenarioKind Kind, bool Owned, bool Handler, DispatchModes? Route, bool Scheduled, bool Outbox)> AllCombinations() {
    DispatchModes?[] routes = [null, DispatchModes.None, DispatchModes.Both, DispatchModes.Local, DispatchModes.LocalNoPersist, DispatchModes.Outbox, DispatchModes.EventStoreOnly];
    foreach (var policy in DispatchPolicies.All) {
      foreach (var kind in Enum.GetValues<ScenarioKind>()) {
        foreach (var owned in new[] { false, true }) {
          foreach (var handler in new[] { false, true }) {
            foreach (var route in routes) {
              foreach (var scheduled in new[] { false, true }) {
                foreach (var outbox in new[] { false, true }) {
                  yield return (policy, kind, owned, handler, route, scheduled, outbox);
                }
              }
            }
          }
        }
      }
    }
  }
}
