namespace Whizbang.Sagas;

/// <summary>
/// App-level configuration for <c>Whizbang.Sagas</c>. Applied at
/// container build via <c>AddWhizbangSagas(opts => …)</c>; all sagas in
/// the process pick up the configured values uniformly.
/// </summary>
/// <remarks>
/// <para>
/// The per-item stream namespace is the only setting that <em>must</em>
/// be configured before any saga operation runs — changing it later
/// re-derives every per-item stream id and orphans existing projection
/// rows. Configure once at startup, never at runtime.
/// </para>
/// </remarks>
public sealed class SagaOptions {

  /// <summary>
  /// Namespace UUID used by <see cref="SagaItemStreams.Of(Guid, string)"/>
  /// (the no-namespace-passed overload) and by every framework call that
  /// derives per-item stream ids by default. Fresh consumers leave this
  /// at the Whizbang default; consumers with pre-existing per-item
  /// streams derived from a different namespace set this to their
  /// historical value so existing rows keep resolving.
  /// </summary>
  public Guid PerItemStreamNamespace { get; set; } = SagaItemStreams.DefaultNamespace;
}
