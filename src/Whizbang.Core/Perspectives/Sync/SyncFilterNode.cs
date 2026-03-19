namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Base type for sync filter tree nodes, enabling AND/OR combinations.
/// </summary>
/// <remarks>
/// Filter nodes form a tree structure that can represent complex filter expressions.
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs</tests>
public abstract record SyncFilterNode;

/// <summary>
/// Filters by a specific stream ID.
/// </summary>
/// <param name="StreamId">The stream ID to filter by.</param>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs</tests>
public sealed record StreamFilter(Guid StreamId) : SyncFilterNode;

/// <summary>
/// Filters by specific event types.
/// </summary>
/// <param name="EventTypes">The event types to filter by.</param>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs</tests>
public sealed record EventTypeFilter(IReadOnlyList<Type> EventTypes) : SyncFilterNode;

/// <summary>
/// Filters to events emitted within the current scope/request.
/// </summary>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs</tests>
public sealed record CurrentScopeFilter : SyncFilterNode;

/// <summary>
/// Matches all pending events without filtering.
/// </summary>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs</tests>
public sealed record AllPendingFilter : SyncFilterNode;

/// <summary>
/// Combines two filters with AND logic (both must match).
/// </summary>
/// <param name="Left">The left filter operand.</param>
/// <param name="Right">The right filter operand.</param>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs</tests>
public sealed record AndFilter(SyncFilterNode Left, SyncFilterNode Right) : SyncFilterNode;

/// <summary>
/// Combines two filters with OR logic (either must match).
/// </summary>
/// <param name="Left">The left filter operand.</param>
/// <param name="Right">The right filter operand.</param>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/SyncFilterBuilderTests.cs</tests>
public sealed record OrFilter(SyncFilterNode Left, SyncFilterNode Right) : SyncFilterNode;
