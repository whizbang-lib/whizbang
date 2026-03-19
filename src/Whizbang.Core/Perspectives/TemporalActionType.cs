namespace Whizbang.Core.Perspectives;

/// <summary>
/// Type of action that triggered a temporal perspective entry.
/// Aligns with SQL Server temporal table patterns for tracking row history.
/// </summary>
/// <docs>fundamentals/perspectives/temporal</docs>
/// <tests>tests/Whizbang.Core.Tests/Lenses/TemporalPerspectiveRowTests.cs</tests>
/// <remarks>
/// <para>
/// Temporal perspectives track the full history of changes to data.
/// Each action type indicates what happened to the entity:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="Insert"/> - New entity was created</description></item>
///   <item><description><see cref="Update"/> - Existing entity was modified</description></item>
///   <item><description><see cref="Delete"/> - Entity was soft-deleted or removed</description></item>
/// </list>
/// </remarks>
public enum TemporalActionType {
  /// <summary>
  /// New entity was created.
  /// This is the first entry in the temporal history for a stream.
  /// </summary>
  Insert = 0,

  /// <summary>
  /// Existing entity was modified.
  /// The entity already existed and its state has changed.
  /// </summary>
  Update = 1,

  /// <summary>
  /// Entity was soft-deleted or removed.
  /// The entity still exists in history but is no longer active.
  /// </summary>
  Delete = 2
}
