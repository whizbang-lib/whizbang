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
public class MessageContextAccessor : IMessageContextAccessor {
  private static readonly AsyncLocal<MessageContextHolder> _messageContextCurrent = new();

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
