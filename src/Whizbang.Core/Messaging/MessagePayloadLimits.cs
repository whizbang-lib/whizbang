using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Configuration;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Enforces the message payload limit where a message is serialized, before it is stored or sent, and
/// again where a message is received from a producer that did not enforce it.
/// </summary>
/// <remarks>
/// The limit that applies is the dispatch's own (<c>DispatchOptions.WithMaxPayloadBytes</c>), else the
/// message type's <c>[MaxPayloadSize]</c> from the generated catalog, else
/// <see cref="WhizbangCoreOptions.MaxMessagePayloadBytes"/>. Zero or less at any level turns it off. The
/// catalog is indexed once at construction, so a check is one dictionary probe and a comparison.
/// </remarks>
/// <docs>fundamentals/messages/payload-size-limit</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/MessagePayloadLimitsTests.cs</tests>
public sealed partial class MessagePayloadLimits {
  private readonly WhizbangCoreOptions _options;
  private readonly IMessagePayloadSizeHook[] _hooks;
  private readonly ILogger<MessagePayloadLimits> _logger;
  private readonly Dictionary<Type, long> _byType = [];
  private readonly Dictionary<string, long> _byName = new(StringComparer.Ordinal);

  /// <summary>Indexes every catalog entry that declares its own limit.</summary>
  public MessagePayloadLimits(
      WhizbangCoreOptions options, IMessageTypeCatalog catalog, IEnumerable<IMessagePayloadSizeHook> hooks,
      ILogger<MessagePayloadLimits> logger) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(catalog);
    ArgumentNullException.ThrowIfNull(hooks);
    ArgumentNullException.ThrowIfNull(logger);
    _options = options;
    _hooks = [.. hooks];
    _logger = logger;
    foreach (var entry in catalog.GetAll().Where(e => e.MaxPayloadBytes is not null)) {
      _byType[entry.Type] = entry.MaxPayloadBytes!.Value;
      _byName[entry.ClrTypeName] = entry.MaxPayloadBytes.Value;
    }
  }

  /// <summary>
  /// The registered instance, or one built from the host's options, catalog and hooks when the host did not
  /// register it, so a dispatcher enforces the limit either way.
  /// </summary>
  public static MessagePayloadLimits Resolve(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return services.GetService<MessagePayloadLimits>() ?? Create(services);
  }

  /// <summary>Builds an instance from what the container holds, with the framework defaults for anything it lacks.</summary>
  public static MessagePayloadLimits Create(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new MessagePayloadLimits(
      services.GetService<WhizbangCoreOptions>() ?? new WhizbangCoreOptions(),
      services.GetService<IMessageTypeCatalog>() ?? NullMessageTypeCatalog.Instance,
      services.GetServices<IMessagePayloadSizeHook>(),
      services.GetService<ILogger<MessagePayloadLimits>>() ?? NullLogger<MessagePayloadLimits>.Instance);
  }

  /// <summary>The size of a serialized payload in UTF-8 bytes, as it is stored and sent.</summary>
  public static long Measure(JsonElement payload) => JsonMarshal.GetRawUtf8Value(payload).Length;

  /// <summary>
  /// Throws <see cref="MessagePayloadTooLargeException"/> when the payload is over the limit that applies
  /// and no hook allowed it, or when a hook rejected it.
  /// </summary>
  public void Enforce(JsonElement payload, Type messageType, Guid messageId, Guid? streamId, long? callLimit = null) {
    ArgumentNullException.ThrowIfNull(messageType);
    var typeLimit = _byType.TryGetValue(messageType, out var limit) ? limit : (long?)null;
    var rejection = _evaluate(Measure(payload), TypeNameFormatter.FormatClrTypeName(messageType), typeLimit, messageId, streamId, callLimit);
    if (rejection is not null) {
      throw rejection;
    }
  }

  /// <summary>
  /// Decides about a payload by its wire type name, without throwing: the rejection, or null when the
  /// payload is accepted. Used where a message is received rather than produced.
  /// </summary>
  public MessagePayloadTooLargeException? Evaluate(long payloadBytes, string messageType, Guid messageId, Guid? streamId, long? callLimit = null) {
    var typeLimit = _byName.TryGetValue(messageType, out var limit) ? limit : (long?)null;
    return _evaluate(payloadBytes, messageType, typeLimit, messageId, streamId, callLimit);
  }

  private MessagePayloadTooLargeException? _evaluate(
      long payloadBytes, string messageType, long? typeLimit, Guid messageId, Guid? streamId, long? callLimit) {
    var (limitBytes, source) = (callLimit, typeLimit) switch {
      ( { } call, _) => (call, PayloadLimitSource.Call),
      (null, { } type) => (type, PayloadLimitSource.MessageType),
      _ => (_options.MaxMessagePayloadBytes ?? 0, PayloadLimitSource.Default),
    };
    if (limitBytes <= 0 || payloadBytes < limitBytes * _options.MessagePayloadWarningRatio) {
      return null;
    }

    var context = new MessagePayloadSizeContext(messageType, messageId, streamId, payloadBytes, limitBytes, source, payloadBytes > limitBytes);
    MessagePayloadSizeDecision? rejected = null;
    var allowed = false;
    foreach (var hook in _hooks) {
      var decision = hook.Evaluate(context);
      rejected ??= decision.Verdict == false ? decision : null;
      allowed |= decision.Verdict == true;
    }

    if (rejected is { } reject) {
      return new MessagePayloadTooLargeException(context, reject.UserMessage, reject.ErrorCode);
    }
    if (context.OverLimit && !allowed) {
      return new MessagePayloadTooLargeException(context);
    }
    if (context.OverLimit) {
      LogOverLimitAllowed(_logger, messageType, messageId, payloadBytes, limitBytes);
    } else {
      LogNearLimit(_logger, messageType, messageId, payloadBytes, limitBytes);
    }
    return null;
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "{MessageType} (message_id={MessageId}) has a {PayloadBytes}-byte payload, near its {LimitBytes}-byte limit")]
  private static partial void LogNearLimit(ILogger logger, string messageType, Guid messageId, long payloadBytes, long limitBytes);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "{MessageType} (message_id={MessageId}) has a {PayloadBytes}-byte payload, over its {LimitBytes}-byte limit; a hook allowed it")]
  private static partial void LogOverLimitAllowed(ILogger logger, string messageType, Guid messageId, long payloadBytes, long limitBytes);
}
