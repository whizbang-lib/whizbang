namespace Whizbang.Core;

/// <summary>
/// Populates the <c>wh_message_type_registry</c> table from the compile-time
/// <see cref="IMessageTypeCatalog"/> at startup.
/// </summary>
/// <remarks>
/// <para>
/// For each concrete IMessage / IPerspectiveFor&lt;&gt; type in the catalog:
/// <list type="bullet">
///   <item>If the type has a <c>pinned_id</c>, upsert is keyed by <c>pinned_id</c>.
///         If an existing row's <c>clr_type_name</c> differs, a warning is logged and the
///         registry row is <b>not</b> overwritten — only the rename tool reconciles drift.</item>
///   <item>If the type has no <c>pinned_id</c>, upsert is keyed by <c>clr_type_name</c>.</item>
/// </list>
/// </para>
/// </remarks>
/// <docs>core-concepts/pinned-identity</docs>
public interface IMessageTypeRegistryPopulator {
  /// <summary>
  /// Reconciles the registry with the catalog. Safe to call on every startup.
  /// </summary>
  Task PopulateAsync(CancellationToken cancellationToken = default);
}
