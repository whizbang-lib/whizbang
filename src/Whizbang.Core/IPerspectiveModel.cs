namespace Whizbang.Core;

/// <summary>
/// Provides access to the current materialized data of a perspective.
/// </summary>
/// <typeparam name="TModel">The perspective's data model type.</typeparam>
public interface IPerspectiveModel<out TModel> where TModel : class {
  /// <summary>
  /// Returns the current materialized state of the perspective.
  /// </summary>
  /// <returns>The current data model instance.</returns>
  TModel CurrentData();
}
