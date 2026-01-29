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
/// <docs>core-concepts/model-action</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/ModelActionTests.cs</tests>
/// <example>
/// <para><strong>Returning ModelAction from Apply:</strong></para>
/// <code>
/// public class OrderPerspective : IPerspectiveFor&lt;OrderView, OrderCancelled&gt; {
///   public ModelAction Apply(OrderView current, OrderCancelled @event) {
///     return ModelAction.Delete;  // Soft delete - sets DeletedAt
///   }
/// }
/// </code>
/// </example>
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
  Purge = 2
}
