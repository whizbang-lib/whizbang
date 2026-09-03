namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Base type for sync filter tree nodes, enabling AND/OR combinations.
/// </summary>
/// <remarks>
/// Filter nodes form a tree structure that can represent complex filter expressions.
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs</tests>
#pragma warning disable S2094 // Intentional: discriminated union base record for filter tree pattern matching
public abstract record SyncFilterNode;
#pragma warning restore S2094

/// <summary>
/// Filters by a specific stream ID.
/// </summary>
/// <param name="StreamId">The stream ID to filter by.</param>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs:StreamFilter_StoresStreamIdAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs:SyncFilter_ForStream_CreatesBuilderWithStreamFilterAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/ScopedEventTrackerTests.cs:ScopedEventTracker_GetEmittedEvents_WithStreamFilter_ReturnsMatchingAsync</tests>
public sealed record StreamFilter(Guid StreamId) : SyncFilterNode;

/// <summary>
/// Filters by specific event types.
/// </summary>
/// <param name="EventTypes">The event types to filter by.</param>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs:EventTypeFilter_StoresEventTypesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs:SyncFilter_ForEventTypes_Generic_CreatesBuilderAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/ScopedEventTrackerTests.cs:ScopedEventTracker_GetEmittedEvents_WithEventTypeFilter_ReturnsMatchingAsync</tests>
public sealed record EventTypeFilter(IReadOnlyList<Type> EventTypes) : SyncFilterNode;

/// <summary>
/// Filters to events emitted within the current scope/request.
/// </summary>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs:CurrentScopeFilter_CanBeCreatedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs:SyncFilter_CurrentScope_CreatesBuilderAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/ScopedEventTrackerTests.cs:ScopedEventTracker_GetEmittedEvents_WithCurrentScopeFilter_ReturnsAllAsync</tests>
public sealed record CurrentScopeFilter : SyncFilterNode;

/// <summary>
/// Matches all pending events without filtering.
/// </summary>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs:AllPendingFilter_CanBeCreatedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs:SyncFilter_All_CreatesBuilderAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/ScopedEventTrackerTests.cs:ScopedEventTracker_GetEmittedEvents_WithAllPendingFilter_ReturnsAllAsync</tests>
public sealed record AllPendingFilter : SyncFilterNode;

/// <summary>
/// Combines two filters with AND logic (both must match).
/// </summary>
/// <param name="Left">The left filter operand.</param>
/// <param name="Right">The right filter operand.</param>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs:AndFilter_StoresLeftAndRightAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs:SyncFilterBuilder_And_CombinesFiltersAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/ScopedEventTrackerTests.cs:ScopedEventTracker_GetEmittedEvents_WithAndFilter_ReturnsIntersectionAsync</tests>
public sealed record AndFilter(SyncFilterNode Left, SyncFilterNode Right) : SyncFilterNode;

/// <summary>
/// Combines two filters with OR logic (either must match).
/// </summary>
/// <param name="Left">The left filter operand.</param>
/// <param name="Right">The right filter operand.</param>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs:OrFilter_StoresLeftAndRightAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/ScopedEventTrackerTests.cs:ScopedEventTracker_GetEmittedEvents_WithOrFilter_ReturnsUnionAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderFullCoverageTests.cs:OrEventTypes_2Generic_ContainsCorrectTypesAsync</tests>
public sealed record OrFilter(SyncFilterNode Left, SyncFilterNode Right) : SyncFilterNode;
