namespace Whizbang.Core.Perspectives;

/// <summary>
/// Specifies what action to take on a perspective model after an Apply method executes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Usage Pattern:</strong>
/// </para>
/// <para>
/// This enum is returned from Apply methods (directly or via <see cref="ApplyResult{TModel}"/>)
/// to indicate the lifecycle action for the perspective model.
/// </para>
/// <para>
/// <strong>Delete vs Purge:</strong>
/// </para>
/// <para>
/// <c>Delete</c> performs a soft delete by setting the model's <c>DeletedAt</c> timestamp,
/// preserving the row for audit purposes. The model must have a <c>DateTimeOffset? DeletedAt</c>
/// property for this to work.
/// </para>
/// <para>
/// <c>Purge</c> performs a hard delete by removing the row from the database entirely.
/// Use this only when data retention is not required.
/// </para>
/// </remarks>
/// <example>
/// <para><strong>Returning ModelAction from Apply:</strong></para>
/// <code>
/// public class OrderPerspective : IPerspectiveFor&lt;OrderView, OrderCanceled&gt; {
///   public ModelAction Apply(OrderView current, OrderCanceled @event) {
///     return ModelAction.Delete;  // Soft delete - sets DeletedAt
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
  /// Soft delete - set the model's <c>DeletedAt</c> timestamp.
  /// The row remains in the database but is marked as deleted.
  /// Requires the model to have a <c>DateTimeOffset? DeletedAt</c> property.
  /// </summary>
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
