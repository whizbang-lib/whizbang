using Microsoft.Extensions.Options;
using Whizbang.Core.Pipeline;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// Pipeline behavior that automatically emits <see cref="CommandAudited"/> system events
/// after commands are processed by receptors.
/// </summary>
/// <remarks>
/// <para>
/// This behavior wraps command execution and emits audit events only when:
/// - Command auditing is enabled via <see cref="SystemEventOptions.EnableCommandAudit"/>
/// - The command type is not marked with <c>[AuditEvent(Exclude = true)]</c>
/// </para>
/// <para>
/// The receptor name is extracted from the message context metadata under the key
/// <c>ReceptorName</c>. If not present, the behavior uses the command type name as fallback.
/// </para>
/// <para>
/// This behavior should be registered as a pipeline behavior in the DI container:
/// <code>
/// services.AddSingleton(typeof(IPipelineBehavior&lt;,&gt;), typeof(CommandAuditPipelineBehavior&lt;,&gt;));
/// </code>
/// </para>
/// </remarks>
/// <typeparam name="TCommand">The command type being processed.</typeparam>
/// <typeparam name="TResponse">The response type from the receptor.</typeparam>
/// <docs>core-concepts/system-events#command-auditing</docs>
public sealed class CommandAuditPipelineBehavior<TCommand, TResponse> : PipelineBehavior<TCommand, TResponse>
    where TCommand : notnull {
  private readonly ISystemEventEmitter _emitter;
  private readonly SystemEventOptions _options;
  private readonly IMessageContext? _context;

  /// <summary>
  /// Creates a new command audit pipeline behavior.
  /// </summary>
  /// <param name="emitter">The system event emitter for audit events.</param>
  /// <param name="options">System event configuration options.</param>
  /// <param name="context">Optional message context for extracting receptor name and scope.</param>
  public CommandAuditPipelineBehavior(
      ISystemEventEmitter emitter,
      IOptions<SystemEventOptions> options,
      IMessageContext? context = null) {
    _emitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _context = context;
  }

  /// <inheritdoc />
  public override async Task<TResponse> HandleAsync(
      TCommand request,
      Func<Task<TResponse>> continuation,
      CancellationToken cancellationToken = default) {
    // Execute the next behavior or handler
    var response = await ExecuteNextAsync(continuation);

    // Check if command auditing is enabled
    if (!_options.CommandAuditEnabled) {
      return response;
    }

    // Check if this command type should be excluded from audit
    if (_emitter.ShouldExcludeFromAudit(typeof(TCommand))) {
      return response;
    }

    // Extract receptor name from context metadata, fallback to command type name
    var receptorName = _extractReceptorName();

    // Emit the audit event
    await _emitter.EmitCommandAuditedAsync(
        request,
        response,
        receptorName,
        _context,
        cancellationToken);

    return response;
  }

  /// <summary>
  /// Extracts the receptor name from context metadata.
  /// Falls back to a generated name based on the command type.
  /// </summary>
  private string _extractReceptorName() {
    // Try to get from context metadata
    if (_context?.Metadata.TryGetValue("ReceptorName", out var receptorNameObj) == true &&
        receptorNameObj is string receptorName &&
        !string.IsNullOrEmpty(receptorName)) {
      return receptorName;
    }

    // Fallback: Generate receptor name from command type
    // CreateOrderCommand → CreateOrderReceptor
    var commandTypeName = typeof(TCommand).Name;
    if (commandTypeName.EndsWith("Command", StringComparison.Ordinal)) {
      return commandTypeName[..^7] + "Receptor";
    }

    return commandTypeName + "Receptor";
  }
}
