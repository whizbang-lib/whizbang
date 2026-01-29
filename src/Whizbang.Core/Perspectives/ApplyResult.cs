#pragma warning disable CA1000 // Do not declare static members on generic types - factory methods provide cleaner API

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Result of applying an event to a perspective, containing an optional model and action.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Usage Pattern:</strong>
/// </para>
/// <para>
/// This struct provides a unified way to return both a model and an action from Apply methods.
/// It supports implicit conversions from common patterns for cleaner code.
/// </para>
/// <para>
/// <strong>Implicit Conversions:</strong>
/// </para>
/// <list type="bullet">
/// <item><c>TModel</c> → <c>ApplyResult&lt;TModel&gt;</c> with <c>Action = None</c></item>
/// <item><c>ModelAction</c> → <c>ApplyResult&lt;TModel&gt;</c> with <c>Model = null</c></item>
/// <item><c>(TModel?, ModelAction)</c> → <c>ApplyResult&lt;TModel&gt;</c></item>
/// </list>
/// <para>
/// <strong>Return Type Semantics:</strong>
/// </para>
/// <list type="bullet">
/// <item><c>Model != null, Action = None</c> → Update the model</item>
/// <item><c>Model = null, Action = None</c> → No change (skip update)</item>
/// <item><c>Action = Delete</c> → Soft delete (set DeletedAt)</item>
/// <item><c>Action = Purge</c> → Hard delete (remove row)</item>
/// </list>
/// </remarks>
/// <typeparam name="TModel">The perspective model type.</typeparam>
/// <docs>core-concepts/model-action</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/ApplyResultTests.cs</tests>
/// <example>
/// <para><strong>Using static factory methods:</strong></para>
/// <code>
/// public ApplyResult&lt;OrderView&gt; Apply(OrderView current, OrderArchived @event) {
///   if (@event.ShouldPurge)
///     return ApplyResult&lt;OrderView&gt;.Purge();
///   return ApplyResult&lt;OrderView&gt;.Update(current with { ArchivedAt = @event.ArchivedAt });
/// }
/// </code>
/// </example>
/// <example>
/// <para><strong>Using implicit conversions:</strong></para>
/// <code>
/// // Return model directly - implicit conversion to ApplyResult with None action
/// public ApplyResult&lt;OrderView&gt; Apply(OrderView current, OrderUpdated @event) {
///   return current with { UpdatedAt = @event.UpdatedAt };
/// }
///
/// // Return action directly - implicit conversion to ApplyResult with null model
/// public ApplyResult&lt;OrderView&gt; Apply(OrderView current, OrderCancelled @event) {
///   return ModelAction.Delete;
/// }
///
/// // Return tuple - implicit conversion to ApplyResult
/// public ApplyResult&lt;OrderView&gt; Apply(OrderView current, OrderArchived @event) {
///   return (current with { ArchivedAt = @event.ArchivedAt }, ModelAction.None);
/// }
/// </code>
/// </example>
public readonly struct ApplyResult<TModel> where TModel : class {
  /// <summary>
  /// Gets the model to update, or null if no update is needed or deletion is requested.
  /// </summary>
  public TModel? Model { get; }

  /// <summary>
  /// Gets the action to perform on the model.
  /// </summary>
  public ModelAction Action { get; }

  /// <summary>
  /// Initializes a new instance of the <see cref="ApplyResult{TModel}"/> struct.
  /// </summary>
  /// <param name="model">The model to update, or null.</param>
  /// <param name="action">The action to perform. Defaults to <see cref="ModelAction.None"/>.</param>
  public ApplyResult(TModel? model, ModelAction action = ModelAction.None) {
    Model = model;
    Action = action;
  }

  /// <summary>
  /// Creates a result indicating no change (skip update).
  /// </summary>
  /// <returns>An <see cref="ApplyResult{TModel}"/> with null model and <see cref="ModelAction.None"/>.</returns>
  public static ApplyResult<TModel> None() => new(null, ModelAction.None);

  /// <summary>
  /// Creates a result indicating soft delete (set DeletedAt timestamp).
  /// </summary>
  /// <returns>An <see cref="ApplyResult{TModel}"/> with null model and <see cref="ModelAction.Delete"/>.</returns>
  public static ApplyResult<TModel> Delete() => new(null, ModelAction.Delete);

  /// <summary>
  /// Creates a result indicating hard delete (remove row from database).
  /// </summary>
  /// <returns>An <see cref="ApplyResult{TModel}"/> with null model and <see cref="ModelAction.Purge"/>.</returns>
  public static ApplyResult<TModel> Purge() => new(null, ModelAction.Purge);

  /// <summary>
  /// Creates a result indicating the model should be updated.
  /// </summary>
  /// <param name="model">The updated model.</param>
  /// <returns>An <see cref="ApplyResult{TModel}"/> with the specified model and <see cref="ModelAction.None"/>.</returns>
  public static ApplyResult<TModel> Update(TModel model) => new(model, ModelAction.None);

  /// <summary>
  /// Implicitly converts a model to an <see cref="ApplyResult{TModel}"/> with <see cref="ModelAction.None"/>.
  /// </summary>
  /// <param name="model">The model to convert.</param>
  public static implicit operator ApplyResult<TModel>(TModel model) => new(model);

  /// <summary>
  /// Implicitly converts a <see cref="ModelAction"/> to an <see cref="ApplyResult{TModel}"/> with null model.
  /// </summary>
  /// <param name="action">The action to convert.</param>
  public static implicit operator ApplyResult<TModel>(ModelAction action) => new(null, action);

  /// <summary>
  /// Implicitly converts a tuple to an <see cref="ApplyResult{TModel}"/>.
  /// </summary>
  /// <param name="tuple">The tuple containing model and action.</param>
  public static implicit operator ApplyResult<TModel>((TModel?, ModelAction) tuple) => new(tuple.Item1, tuple.Item2);
}
