namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Provides access to the current sync context within a scoped request.
/// Similar to IHttpContextAccessor pattern.
/// </summary>
/// <docs>core-concepts/perspectives/perspective-sync#sync-context</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/SyncContextAccessorTests.cs</tests>
public interface ISyncContextAccessor {
  /// <summary>
  /// Gets or sets the current sync context.
  /// Set by ReceptorInvoker after sync completes before invoking the receptor.
  /// </summary>
  SyncContext? Current { get; set; }
}

/// <summary>
/// Default implementation of <see cref="ISyncContextAccessor"/>.
/// Uses AsyncLocal for async flow.
/// </summary>
/// <remarks>
/// <para>
/// For scoped services, resolve ISyncContextAccessor via DI and use the <see cref="Current"/> property.
/// </para>
/// <para>
/// For singleton services that cannot resolve scoped ISyncContextAccessor,
/// use <see cref="CurrentContext"/> which provides direct access to the static AsyncLocal.
/// </para>
/// </remarks>
public class SyncContextAccessor : ISyncContextAccessor {
  private static readonly AsyncLocal<SyncContextHolder> _syncContextCurrent = new();

  /// <summary>
  /// Static accessor for the current sync context.
  /// Use this from singleton services that cannot resolve the scoped ISyncContextAccessor.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This provides direct access to the ambient context without requiring DI resolution.
  /// Use sparingly - prefer the scoped ISyncContextAccessor for proper DI patterns.
  /// </para>
  /// </remarks>
  /// <docs>core-concepts/perspectives/perspective-sync#sync-context</docs>
  public static SyncContext? CurrentContext {
    get => _syncContextCurrent.Value?.Context;
    set {
      // Always create a new holder to ensure isolation between async flows
      // This prevents child tasks from affecting parent contexts
      _syncContextCurrent.Value = new SyncContextHolder { Context = value };
    }
  }

  /// <inheritdoc />
  public SyncContext? Current {
    get => _syncContextCurrent.Value?.Context;
    set {
      // Always create a new holder to ensure isolation between async flows
      // This prevents child tasks from affecting parent contexts
      _syncContextCurrent.Value = new SyncContextHolder { Context = value };
    }
  }

  private sealed class SyncContextHolder {
    public SyncContext? Context;
  }
}
