// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Observability;

/// <summary>One policy decision as a route records it: which policy, which rule, and whether it matched.</summary>
/// <param name="PolicyName">The policy's name.</param>
/// <param name="Rule">The rule evaluated.</param>
/// <param name="Matched">Whether the rule matched.</param>
/// <docs>fundamentals/persistence/observability#routes-and-traces</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RouteTraceStoreTests.cs:GetRoute_ReturnsTheNormalizedPath_AndItsStatisticsAsync</tests>
public sealed record RoutePolicyDecision(string PolicyName, string Rule, bool Matched);

/// <summary>One hop of a route: the fields of a hop that stay the same for every event taking the route.</summary>
/// <param name="Type">Whether the hop is the event's own (<see cref="HopType.Current"/>) or carried from its cause.</param>
/// <param name="ServiceName">The service the hop ran in; empty when the hop named none.</param>
/// <param name="Topic">The topic; empty when none.</param>
/// <param name="ExecutionStrategy">The execution strategy; empty when none.</param>
/// <param name="CallerClass">The caller's class (its file's name); empty when none.</param>
/// <param name="CallerMember">The caller's member; empty when none.</param>
/// <param name="PolicyDecisions">The policy decisions taken on the hop, in order.</param>
/// <docs>fundamentals/persistence/observability#routes-and-traces</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RouteTraceStoreTests.cs:GetRoute_ReturnsTheNormalizedPath_AndItsStatisticsAsync</tests>
public sealed record RouteHop(
  HopType Type,
  string ServiceName,
  string Topic,
  string ExecutionStrategy,
  string CallerClass,
  string CallerMember,
  IReadOnlyList<RoutePolicyDecision> PolicyDecisions);

/// <summary>
/// The durations of a route's occurrences, an occurrence's duration being the sum of its hops' durations. Only
/// occurrences with a duration count.
/// </summary>
/// <param name="Count">Occurrences with a duration.</param>
/// <param name="TotalMilliseconds">Their durations added up.</param>
/// <param name="MinMilliseconds">The shortest, or null when none had a duration.</param>
/// <param name="MaxMilliseconds">The longest, or null when none had a duration.</param>
/// <param name="Buckets">
/// Counts by duration, six buckets: up to 1 ms, 10 ms, 100 ms, 1 s, 10 s, and longer.
/// </param>
/// <docs>fundamentals/persistence/observability#routes-and-traces</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Distill_AddsDurationStatistics_InOneUpdatePerRouteAsync</tests>
public sealed record RouteDurationStatistics(
  long Count,
  double TotalMilliseconds,
  double? MinMilliseconds,
  double? MaxMilliseconds,
  IReadOnlyList<long> Buckets);

/// <summary>A route: the normalized path events of one type take, stored once and counted.</summary>
/// <param name="RouteId">The route id, as the event rows carry it (<c>rid</c>).</param>
/// <param name="EventType">The event type's name, without its assembly.</param>
/// <param name="Hops">The normalized hops, in order.</param>
/// <param name="Occurrences">How many distilled events took the route.</param>
/// <param name="FirstSeen">When the first of them was staged.</param>
/// <param name="LastSeen">When the latest of them was staged.</param>
/// <param name="Durations">Their duration statistics.</param>
/// <docs>fundamentals/persistence/observability#routes-and-traces</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RouteTraceStoreTests.cs:GetRoute_ReturnsTheNormalizedPath_AndItsStatisticsAsync</tests>
public sealed record RouteInfo(
  Guid RouteId,
  string EventType,
  IReadOnlyList<RouteHop> Hops,
  long Occurrences,
  DateTimeOffset FirstSeen,
  DateTimeOffset LastSeen,
  RouteDurationStatistics Durations);

/// <summary>
/// The architecture view of how events travel: each route stored once, with its occurrence counts and durations, kept
/// long after the individual traces (<see cref="ITraceStore"/>) are gone.
/// </summary>
/// <remarks>
/// <para>
/// Routes are distilled by maintenance from the full metadata the write paths stage, so a route appears once the
/// staged form of an event taking it has been distilled. Every event row carries its route id from the moment it is
/// written, so a route read later still answers how that event got here.
/// </para>
/// <para>
/// This store and <see cref="ITraceStore"/> are registered separately; a host can register either alone.
/// </para>
/// </remarks>
/// <docs>fundamentals/persistence/observability#routes-and-traces</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RouteTraceStoreTests.cs</tests>
public interface IRouteStore {
  /// <summary>The route with <paramref name="routeId"/>, or null when none is stored.</summary>
  /// <param name="routeId">The route id.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>The route, or null.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RouteTraceStoreTests.cs:GetRoute_ReturnsTheNormalizedPath_AndItsStatisticsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RouteTraceStoreTests.cs:GetRoute_Unknown_IsNullAsync</tests>
  Task<RouteInfo?> GetRouteAsync(Guid routeId, CancellationToken cancellationToken = default);

  /// <summary>The routes events of <paramref name="eventType"/> take, the most taken first.</summary>
  /// <param name="eventType">The event type, assembly-qualified or not.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>The routes; empty when none.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RouteTraceStoreTests.cs:GetRoutes_ByEventType_MostTakenFirstAsync</tests>
  Task<IReadOnlyList<RouteInfo>> GetRoutesAsync(string eventType, CancellationToken cancellationToken = default);

  /// <summary>
  /// The routes behind an apply path (<c>wh_apply_paths.path</c>): the routes of every event type on the path, in the
  /// path's order, each type's most taken first. A run marker (a trailing <c>+</c>) is ignored.
  /// </summary>
  /// <param name="applyPath">The apply path's event types.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>The routes; empty when none.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RouteTraceStoreTests.cs:GetRoutesForApplyPath_ReturnsEachTypesRoutes_InThePathsOrderAsync</tests>
  Task<IReadOnlyList<RouteInfo>> GetRoutesForApplyPathAsync(IReadOnlyList<string> applyPath, CancellationToken cancellationToken = default);
}
