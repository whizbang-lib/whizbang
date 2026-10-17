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
/// <param name="SameAs">The row this one repeats for a sibling policy, or null for an original row.</param>
/// <param name="Behaviors">The caller's overrides.</param>
/// <param name="ResultRequired">True when the caller needs the handler's result.</param>
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
  DispatchRejection ExpectedReject,
  int? SameAs = null,
  DispatchBehaviors Behaviors = DispatchBehaviors.None,
  bool ResultRequired = false) {

  /// <summary>The fixed scheduled time scenarios use.</summary>
  public static readonly DateTimeOffset ScheduledAt = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

  /// <summary>True when the expected outbox copy is marked as already handled here.</summary>
  public bool ExpectedMarked => ExpectedLocal == LocalRun.Now && ExpectedOutbox;

  /// <summary>The situation this row describes.</summary>
  public DispatchSituation Situation => ToSituation(Kind, Owned, Handler, Route, Scheduled, Outbox, Behaviors, ResultRequired);

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
  public static DispatchSituation ToSituation(ScenarioKind kind, bool owned, bool handler, DispatchModes? route, bool scheduled, bool outbox,
      DispatchBehaviors behaviors = DispatchBehaviors.None, bool resultRequired = false) {
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
      OutboxAvailable: outbox,
      behaviors,
      resultRequired);
  }

  /// <inheritdoc />
  public override string ToString() => SameAs is int original
    ? $"{Policy.Name}_Row{Row:D3}_SameAs{original:D2}_{Name}"
    : $"{Policy.Name}_Row{Row:D2}_{Name}";
}

/// <summary>The decision table, one entry per named row.</summary>
public static class DispatchScenarios {
  private const LocalRun NONE = LocalRun.None;
  private const LocalRun NOW = LocalRun.Now;
  private const LocalRun LATER = LocalRun.AtScheduledTime;
  private const DispatchRejection OK = DispatchRejection.None;

  private static DispatchScenario _row(int row, string name, DispatchPolicy policy, ScenarioKind kind,
      bool owned, bool handler, LocalRun local, bool outbox, DispatchRejection reject = OK,
      DispatchModes? route = null, bool scheduled = false, bool hasOutbox = true, bool storeOnly = false,
      DispatchBehaviors behaviors = DispatchBehaviors.None, bool resultRequired = false) =>
    new(row, name, policy, kind, owned, handler, route, scheduled, hasOutbox, local, outbox, storeOnly, reject,
      Behaviors: behaviors, ResultRequired: resultRequired);

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

    List<DispatchScenario> rows = [
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
      _row(34, "Event_Handled_NoOutbox_RunsHereAndDefersOrLogs", publish, E, owned: false, handler: true, NOW, outbox: true, hasOutbox: false),
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

      // Kept in the service (routed local only) with no local handler
      _row(54, "Command_RoutedLocal_NoHandler_Throws", send, C, owned: false, handler: false, NONE, outbox: false, DispatchRejection.NoHandler, route: DispatchModes.Local),
      _row(55, "Command_RoutedLocal_NoHandler_ContinueOverride_DoesNothing", send, C, owned: false, handler: false, NONE, outbox: false, route: DispatchModes.Local, behaviors: DispatchBehaviors.ContinueWhenNoHandler),
      _row(56, "Event_RoutedLocalNoPersist_NoHandler_DoesNothing", publish, E, owned: false, handler: false, NONE, outbox: false, route: DispatchModes.LocalNoPersist),
      _row(57, "Event_RoutedLocalNoPersist_NoHandler_ThrowOverride_Throws", publish, E, owned: false, handler: false, NONE, outbox: false, DispatchRejection.NoHandler, route: DispatchModes.LocalNoPersist, behaviors: DispatchBehaviors.ThrowWhenNoHandler),
      _row(58, "Event_RoutedLocal_NoHandler_IsStored", publish, E, owned: false, handler: false, NONE, outbox: false, route: DispatchModes.Local, storeOnly: true),
      _row(59, "Command_RoutedLocal_NoHandler_Throws", cascade, C, owned: false, handler: false, NONE, outbox: false, DispatchRejection.NoHandler, route: DispatchModes.Local),

      // Overrides of a missing handler
      _row(60, "OwnedCommand_NoHandler_ContinueOverride_DoesNothing", send, C, owned: true, handler: false, NONE, outbox: false, behaviors: DispatchBehaviors.ContinueWhenNoHandler),
      _row(61, "Event_NoHandler_ThrowOverride_Throws", publish, E, owned: false, handler: false, NONE, outbox: false, DispatchRejection.NoHandler, behaviors: DispatchBehaviors.ThrowWhenNoHandler),
      _row(62, "NoHandler_ContinueOverride_DoesNothing", local, C, owned: false, handler: false, NONE, outbox: false, behaviors: DispatchBehaviors.ContinueWhenNoHandler),
      _row(63, "NoHandler_ContinueOverride_ResultRequired_StillThrows", local, C, owned: false, handler: false, NONE, outbox: false, DispatchRejection.NoHandler, behaviors: DispatchBehaviors.ContinueWhenNoHandler, resultRequired: true),
      _row(64, "BothHandlerOverrides_Throws", send, C, owned: false, handler: true, NONE, outbox: false, DispatchRejection.ConflictingBehaviors, behaviors: DispatchBehaviors.ThrowWhenNoHandler | DispatchBehaviors.ContinueWhenNoHandler),

      // Scheduled with no outbox
      _row(65, "Event_Scheduled_NoOutbox_Throws", publish, E, owned: false, handler: true, NONE, outbox: false, DispatchRejection.NoOutbox, scheduled: true, hasOutbox: false),
      _row(66, "Event_Scheduled_NoOutbox_AllowOverride_DefersOrLogs", publish, E, owned: false, handler: true, LATER, outbox: true, scheduled: true, hasOutbox: false, behaviors: DispatchBehaviors.AllowScheduleWithoutOutbox),
      _row(67, "Command_Scheduled_NoOutbox_Throws", send, C, owned: false, handler: true, NONE, outbox: false, DispatchRejection.NoOutbox, scheduled: true, hasOutbox: false),
      _row(68, "Command_Scheduled_NoOutbox_AllowOverride_StillThrows", send, C, owned: false, handler: true, NONE, outbox: false, DispatchRejection.NoOutbox, scheduled: true, hasOutbox: false, behaviors: DispatchBehaviors.AllowScheduleWithoutOutbox),

      // Event kept local and stored, on the send and publish policies
      _row(69, "Event_RoutedLocal_RunsHereAndIsStored", send, E, owned: false, handler: true, NOW, outbox: false, route: DispatchModes.Local, storeOnly: true),
      _row(70, "Event_RoutedLocal_RunsHereAndIsStored", publish, E, owned: false, handler: true, NOW, outbox: false, route: DispatchModes.Local, storeOnly: true),

      // Refusals on the publish and cascade policies
      _row(71, "RoutedNone_Throws", publish, C, owned: false, handler: false, NONE, outbox: false, DispatchRejection.NoRoute, route: DispatchModes.None),
      _row(72, "Command_RoutedEventStoreOnly_Throws", publish, C, owned: false, handler: false, NONE, outbox: false, DispatchRejection.CommandCannotBeStoreOnly, route: DispatchModes.EventStoreOnly),
      _row(73, "Event_RoutedLocal_Scheduled_Throws", publish, E, owned: false, handler: true, NONE, outbox: false, DispatchRejection.CannotScheduleInProcess, route: DispatchModes.Local, scheduled: true),
      _row(74, "RoutedNone_Throws", cascade, C, owned: false, handler: false, NONE, outbox: false, DispatchRejection.NoRoute, route: DispatchModes.None),

      // Contradictory overrides on every policy family
      _row(75, "BothHandlerOverrides_Throws", local, C, owned: false, handler: true, NONE, outbox: false, DispatchRejection.ConflictingBehaviors, behaviors: DispatchBehaviors.ThrowWhenNoHandler | DispatchBehaviors.ContinueWhenNoHandler),
      _row(76, "BothHandlerOverrides_Throws", publish, E, owned: false, handler: true, NONE, outbox: false, DispatchRejection.ConflictingBehaviors, behaviors: DispatchBehaviors.ThrowWhenNoHandler | DispatchBehaviors.ContinueWhenNoHandler),
      _row(77, "BothHandlerOverrides_Throws", cascade, E, owned: false, handler: true, NONE, outbox: false, DispatchRejection.ConflictingBehaviors, behaviors: DispatchBehaviors.ThrowWhenNoHandler | DispatchBehaviors.ContinueWhenNoHandler),

      // Scheduled with no outbox, on the publish and cascade policies
      _row(78, "Event_NoHandler_Scheduled_NoOutbox_AllowOverride_DefersOrLogs", publish, E, owned: false, handler: false, NONE, outbox: true, scheduled: true, hasOutbox: false, behaviors: DispatchBehaviors.AllowScheduleWithoutOutbox),
      _row(79, "Event_NoHandler_Scheduled_NoOutbox_Throws", cascade, E, owned: false, handler: false, NONE, outbox: false, DispatchRejection.NoOutbox, scheduled: true, hasOutbox: false),
      _row(80, "Event_NoHandler_Scheduled_NoOutbox_AllowOverride_DefersOrLogs", cascade, E, owned: false, handler: false, NONE, outbox: true, scheduled: true, hasOutbox: false, behaviors: DispatchBehaviors.AllowScheduleWithoutOutbox),
      _row(81, "Event_Handled_Scheduled_NoOutbox_AllowOverride_RunsWhenItComesBack", cascade, E, owned: false, handler: true, LATER, outbox: true, scheduled: true, hasOutbox: false, behaviors: DispatchBehaviors.AllowScheduleWithoutOutbox),
      _row(82, "Command_RoutedLocal_NoHandler_ContinueOverride_DoesNothing", cascade, C, owned: false, handler: false, NONE, outbox: false, route: DispatchModes.Local, behaviors: DispatchBehaviors.ContinueWhenNoHandler),

      // A caller that needs the handler's result (a typed local invoke) under another selected policy
      _row(83, "ResultRequired_NotHandledHere_Throws", send, C, owned: false, handler: false, NONE, outbox: false, DispatchRejection.ResultUnavailable, resultRequired: true),
      _row(84, "ResultRequired_RoutedOutbox_Throws", send, C, owned: false, handler: true, NONE, outbox: false, DispatchRejection.ResultUnavailable, route: DispatchModes.Outbox, resultRequired: true),
      _row(85, "ResultRequired_Scheduled_Throws", publish, E, owned: false, handler: true, NONE, outbox: false, DispatchRejection.ResultUnavailable, scheduled: true, resultRequired: true),
      _row(86, "ResultRequired_Handled_RunsHereAndGoesOut", send, C, owned: false, handler: true, NOW, outbox: true, resultRequired: true),
      _row(87, "ResultRequired_NotHandledHere_Throws", cascade, C, owned: false, handler: false, NONE, outbox: false, DispatchRejection.ResultUnavailable, resultRequired: true),
    ];

    // A batch policy decides each message exactly as its single-message sibling does, so every row of
    // the sibling is repeated for the batch policy and marked "same as" the original.
    _mirror(rows, from: send, to: sendMany, firstRow: 100);
    _mirror(rows, from: local, to: localMany, firstRow: 200);
    _mirror(rows, from: publish, to: publishMany, firstRow: 300);
    return rows;
  }

  private static void _mirror(List<DispatchScenario> rows, DispatchPolicy from, DispatchPolicy to, int firstRow) {
    var next = firstRow;
    foreach (var original in rows.Where(r => r.Policy == from && r.SameAs is null).ToList()) {
      rows.Add(original with { Row = next++, Policy = to, SameAs = original.Row });
    }
  }

  /// <summary>Every value of each input axis, so every combination can be checked.</summary>
  public static IEnumerable<(DispatchPolicy Policy, ScenarioKind Kind, bool Owned, bool Handler, DispatchModes? Route, bool Scheduled, bool Outbox, DispatchBehaviors Behaviors, bool ResultRequired)> AllCombinations() {
    DispatchBehaviors[] behaviorSets = [
      DispatchBehaviors.None,
      DispatchBehaviors.ThrowWhenNoHandler,
      DispatchBehaviors.ContinueWhenNoHandler,
      DispatchBehaviors.AllowScheduleWithoutOutbox,
      DispatchBehaviors.ThrowWhenNoHandler | DispatchBehaviors.ContinueWhenNoHandler,
    ];
    DispatchModes?[] routes = [null, DispatchModes.None, DispatchModes.Both, DispatchModes.Local, DispatchModes.LocalNoPersist, DispatchModes.Outbox, DispatchModes.EventStoreOnly];
    foreach (var policy in DispatchPolicies.All) {
      foreach (var kind in Enum.GetValues<ScenarioKind>()) {
        foreach (var owned in new[] { false, true }) {
          foreach (var handler in new[] { false, true }) {
            foreach (var route in routes) {
              foreach (var scheduled in new[] { false, true }) {
                foreach (var outbox in new[] { false, true }) {
                  foreach (var behaviors in behaviorSets) {
                    foreach (var resultRequired in new[] { false, true }) {
                      yield return (policy, kind, owned, handler, route, scheduled, outbox, behaviors, resultRequired);
                    }
                  }
                }
              }
            }
          }
        }
      }
    }
  }
}
