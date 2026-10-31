// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Specifies what action to take on a perspective model after an Apply method executes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Usage Pattern:</strong>
/// </para>
/// <para>
/// An Apply returns an action through <see cref="ApplyResult{TModel}"/>, the return
/// <see cref="IPerspectiveWithActionsFor{TModel, TEvent1}"/> declares. <see cref="ApplyResult{TModel}"/> converts
/// implicitly from a <see cref="ModelAction"/>, so <c>return ModelAction.Purge;</c> is a complete Apply body. List the
/// events that delete or purge under <c>IPerspectiveWithActionsFor</c>; one class may implement it alongside
/// <c>IPerspectiveFor</c>, each any number of times.
/// </para>
/// <para>
/// <strong>Delete vs Purge:</strong>
/// </para>
/// <para>
/// <c>Delete</c> keeps the row: the runner stores the model returned with the action, or the current model when none
/// is returned. It sets nothing on the model; to record when the row was deleted, set a field such as
/// <c>DeletedAt</c> on the model you return.
/// </para>
/// <para>
/// <c>Purge</c> performs a hard delete by removing the row from the database entirely.
/// Use this only when data retention is not required.
/// </para>
/// </remarks>
/// <example>
/// <para><strong>Returning an action from Apply:</strong></para>
/// <code>
/// public class OrderPerspective :
///     IPerspectiveFor&lt;OrderView, OrderPlaced&gt;,
///     IPerspectiveWithActionsFor&lt;OrderView, OrderCanceled&gt; {
///   public OrderView Apply(OrderView current, OrderPlaced @event) =&gt; current with { Status = "Placed" };
///
///   public ApplyResult&lt;OrderView&gt; Apply(OrderView current, OrderCanceled @event) {
///     return ModelAction.Delete;  // Keeps the row; converts implicitly to ApplyResult&lt;OrderView&gt;
///   }
/// }
/// </code>
/// </example>
/// <docs>fundamentals/perspectives/perspectives-with-actions</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/ModelActionTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRunnerModelActionTests.cs</tests>
public enum ModelAction {
  /// <summary>
  /// No action - keep the model as-is or use the returned model.
  /// This is the default value.
  /// </summary>
  None = 0,

  /// <summary>
  /// Soft delete - keep the row, with the model returned alongside the action or the current model.
  /// The runner sets nothing on the model: a timestamp such as <c>DeletedAt</c> is the Apply's to set.
  /// </summary>
  /// <docs>fundamentals/perspectives/perspectives-with-actions</docs>
  Delete = 1,

  /// <summary>
  /// Hard delete - remove the model from the database entirely.
  /// Use only when data retention is not required.
  /// </summary>
  /// <remarks>
  /// A purged stream stays purged: the framework records the purge, and a later event on the stream is
  /// skipped (logged and counted) instead of being applied to an empty model. Only an Apply that returns
  /// <see cref="Resurrect"/> brings the row back.
  /// </remarks>
  /// <docs>fundamentals/perspectives/perspectives-with-actions#purge-stays-purged</docs>
  Purge = 2,

  /// <summary>
  /// Recreate the row of a purged stream with the returned model, and forget the purge. Use it on the
  /// events that legitimately start the stream over (a reopen, a restore). On a stream that is not purged
  /// it is an ordinary update. A <c>Resurrect</c> with no model is skipped like any other event on a
  /// purged stream.
  /// </summary>
  /// <docs>fundamentals/perspectives/perspectives-with-actions#purge-stays-purged</docs>
  Resurrect = 3
}
