// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Archival;

namespace Whizbang.Core.Attributes;

/// <summary>
/// Declares what happens to this perspective's rows when their stream is archived whole (#1739): kept in place (the
/// default), dropped, or archived beside the events and brought back on restore.
/// </summary>
/// <remarks>
/// The perspective-runner generator reads the attribute at compile time and registers the policy through a
/// <c>[ModuleInitializer]</c> (<see cref="ArchivedStreamRowPolicies"/>), so nothing is read by reflection.
/// <c>Whizbang:Perspectives:ArchivedStreamRows:&lt;Perspective&gt;</c> overrides it per perspective. The policy acts on the
/// rows of the perspective's model: when several perspectives fold one model and declare different policies, the least
/// destructive wins (Keep, then Archive, then Drop).
/// </remarks>
/// <example>
/// <code>
/// [ArchivedStreamRows(ArchivedStreamRowPolicy.Archive)]
/// public sealed class OrderSummaryPerspective : IPerspectiveFor&lt;OrderSummary, OrderPlaced&gt; { ... }
/// </code>
/// </example>
/// <param name="policy">What happens to the perspective's row of an archived stream.</param>
/// <docs>fundamentals/events/archive-store#perspective-rows</docs>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveRunnerGeneratorArchivedStreamRowsTests.cs</tests>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ArchivedStreamRowsAttribute(ArchivedStreamRowPolicy policy) : Attribute {
  /// <summary>What happens to the perspective's row of an archived stream.</summary>
  public ArchivedStreamRowPolicy Policy { get; } = policy;
}
