namespace Whizbang.Core.Security;

/// <summary>
/// Provides access to the current message context within a scoped request.
/// Similar to IHttpContextAccessor pattern.
/// </summary>
/// <docs>core-concepts/message-security#message-context-accessor</docs>
public interface IMessageContextAccessor {
  /// <summary>
  /// Gets or sets the current message context.
  /// Set by ReceptorInvoker before invoking the receptor.
  /// </summary>
  IMessageContext? Current { get; set; }
}

/// <summary>
/// Default implementation of <see cref="IMessageContextAccessor"/>.
/// Uses AsyncLocal for async flow.
/// </summary>
/// <remarks>
/// <para>
/// For scoped services, resolve IMessageContextAccessor via DI and use the <see cref="Current"/> property.
/// </para>
/// <para>
/// For singleton services (e.g., Dispatcher) that cannot resolve scoped IMessageContextAccessor,
/// use <see cref="CurrentContext"/> which provides direct access to the static AsyncLocal.
/// </para>
/// </remarks>
public class MessageContextAccessor : IMessageContextAccessor {
  private static readonly AsyncLocal<MessageContextHolder> _messageContextCurrent = new();

  /// <summary>
  /// Static accessor for the current message context.
  /// Use this from singleton services that cannot resolve the scoped IMessageContextAccessor.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This provides direct access to the ambient context without requiring DI resolution.
  /// Use sparingly - prefer the scoped IMessageContextAccessor for proper DI patterns.
  /// </para>
  /// <para>
  /// Primary use case: Singleton services (e.g., Dispatcher cascade path) that need to set
  /// message context but cannot resolve scoped services.
  /// </para>
  /// </remarks>
  /// <docs>core-concepts/message-security#message-context-accessor</docs>
  public static IMessageContext? CurrentContext {
    get => _messageContextCurrent.Value?.Context;
    set {
      var holder = _messageContextCurrent.Value;
      if (holder != null) {
        holder.Context = null;
      }
      if (value != null) {
        _messageContextCurrent.Value = new MessageContextHolder { Context = value };
      }
    }
  }

  /// <inheritdoc />
  public IMessageContext? Current {
    get => _messageContextCurrent.Value?.Context;
    set {
      var holder = _messageContextCurrent.Value;
      if (holder != null) {
        holder.Context = null;
      }
      if (value != null) {
        _messageContextCurrent.Value = new MessageContextHolder { Context = value };
      }
    }
  }

  private sealed class MessageContextHolder {
    public IMessageContext? Context;
  }
}
