using Microsoft.Extensions.Options;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// Default implementation of <see cref="ITransportPublishFilter"/> that respects
/// the <see cref="SystemEventOptions.LocalOnly"/> setting.
/// </summary>
/// <remarks>
/// <para>
/// This filter implements the transport routing rules:
/// </para>
/// <list type="bullet">
/// <item>
/// <description><strong>Domain events</strong>: Always flow through transport for cross-service communication</description>
/// </item>
/// <item>
/// <description><strong>System events</strong>: Respect <c>LocalOnly</c> setting (default: stay local)</description>
/// </item>
/// </list>
/// <para>
/// The <c>LocalOnly</c> pattern ensures that audit events and other system events don't
/// create unnecessary network traffic or duplicate storage across services. Each service
/// maintains its own local audit trail.
/// </para>
/// <para>
/// For centralized monitoring scenarios where system events need to be aggregated,
/// call <c>options.Broadcast()</c> to set <c>LocalOnly = false</c>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Default: LocalOnly = true - system events stay local
/// services.AddWhizbang(options => {
///   options.SystemEvents.EnableAudit();
///   // LocalOnly is true by default
/// });
///
/// // Broadcast mode: system events flow through transport
/// services.AddWhizbang(options => {
///   options.SystemEvents.EnableAll();
///   options.SystemEvents.Broadcast(); // Sets LocalOnly = false
/// });
/// </code>
/// </example>
/// <docs>core-concepts/system-events#transport-filtering</docs>
public sealed class SystemEventTransportFilter : ITransportPublishFilter {
  private readonly SystemEventOptions _options;

  /// <summary>
  /// Creates a new transport filter with the specified options.
  /// </summary>
  /// <param name="options">System event configuration options.</param>
  public SystemEventTransportFilter(IOptions<SystemEventOptions> options) {
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  }

  /// <inheritdoc />
  public bool ShouldPublishToTransport(object message) {
    // Domain events always publish - they're the core of cross-service communication
    if (message is not ISystemEvent) {
      return true;
    }

    // System events respect LocalOnly setting
    // LocalOnly = true (default) → don't publish to transport
    // LocalOnly = false (Broadcast()) → publish to transport
    return !_options.LocalOnly;
  }

  /// <inheritdoc />
  public bool ShouldReceiveFromTransport(Type messageType) {
    ArgumentNullException.ThrowIfNull(messageType);

    // Domain events always received - they're the core of cross-service communication
    if (!typeof(ISystemEvent).IsAssignableFrom(messageType)) {
      return true;
    }

    // System events respect LocalOnly setting
    // LocalOnly = true (default) → don't receive from transport (each service has its own)
    // LocalOnly = false (Broadcast()) → receive from transport (centralized monitoring)
    return !_options.LocalOnly;
  }
}
