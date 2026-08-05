using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Observability;

/// <summary>
/// Tiny <see cref="IHostedService"/> whose only job is to resolve
/// <see cref="UnobservedExceptionDiagnostics"/> from DI so its constructor runs at process
/// startup — before any other hosted service fires a Task. Without this warm-up, the
/// singleton is lazy-constructed and the global hooks may not be in place when the first
/// abandoned-Task exception happens.
/// </summary>
public sealed class UnobservedExceptionDiagnosticsWarmUp : IHostedService {
  private readonly UnobservedExceptionDiagnostics _diagnostics;

  /// <summary>Constructor — taking the diagnostics dependency triggers its construction.</summary>
  public UnobservedExceptionDiagnosticsWarmUp(UnobservedExceptionDiagnostics diagnostics) {
    _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
  }

  /// <inheritdoc/>
  public Task StartAsync(CancellationToken cancellationToken) {
    // The diagnostics subscribes its handlers in its constructor. Touching the field here
    // keeps the JIT from dead-stripping the dependency in trim/AOT scenarios.
    _ = _diagnostics;
    return Task.CompletedTask;
  }

  /// <inheritdoc/>
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Process-wide diagnostic that wires up <see cref="TaskScheduler.UnobservedTaskException"/>
/// (and optionally <see cref="AppDomain.FirstChanceException"/>) so exceptions raised on
/// abandoned <see cref="Task"/> instances — fire-and-forget background work, race-condition
/// losers — surface to a structured logger instead of vanishing silently on GC.
/// </summary>
/// <remarks>
/// <para>
/// Motivation (a production forensic investigation): a stuck outbox row in a consumer's
/// service was retried hundreds of times across two days with <c>wh_outbox.error</c>
/// staying NULL. Whizbang's worker-level
/// catches all log on failure, but the OutboxDrainWorker's publish call is wrapped in a
/// cooperative cancellation token — if the transport SDK ignores the token, the await
/// never returns, no exception is thrown, and the worker silently sits. Adjacent issue: if
/// a fire-and-forget <c>Task.Run</c> (e.g., a detached lifecycle stage) throws an exception
/// after its registered catch handler completes, the exception escapes as an
/// UnobservedTaskException on the next GC pass. Without a registered handler that's a
/// silent failure — operationally identical to the cooperative-cancel hang.
/// </para>
/// <para>
/// Registered as a singleton inside <c>AddWhizbangWorkers</c>; the constructor performs the
/// process-wide hook subscription so consumers don't need to opt in. Multiple instances are
/// guarded against by the static once-flag — the second instantiation is a no-op.
/// </para>
/// </remarks>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
public sealed class UnobservedExceptionDiagnostics : IDisposable {
  private static readonly Lock _gate = new();
  private static UnobservedExceptionDiagnostics? _registered;

  private readonly ILogger<UnobservedExceptionDiagnostics> _logger;
  private readonly UnobservedExceptionDiagnosticsOptions _options;
  private readonly EventHandler<UnobservedTaskExceptionEventArgs> _unobservedHandler;
  private readonly EventHandler<FirstChanceExceptionEventArgs>? _firstChanceHandler;
  private bool _disposed;

  /// <summary>Constructor — subscribes the process-wide handlers on first instantiation.</summary>
  public UnobservedExceptionDiagnostics(
      ILogger<UnobservedExceptionDiagnostics> logger,
      IOptions<UnobservedExceptionDiagnosticsOptions> options) {
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    _unobservedHandler = (sender, args) => _onUnobservedTaskException(args);

    lock (_gate) {
      if (_registered is not null) {
        // Another instance already owns the subscription. Wire null handlers so Dispose is a no-op.
        return;
      }
      TaskScheduler.UnobservedTaskException += _unobservedHandler;
      if (_options.EnableFirstChanceExceptionLogging) {
        _firstChanceHandler = (sender, args) => _onFirstChanceException(args);
        AppDomain.CurrentDomain.FirstChanceException += _firstChanceHandler;
      }
      _registered = this;
    }
  }

  private void _onUnobservedTaskException(UnobservedTaskExceptionEventArgs args) {
    // SetObserved prevents the legacy CLR behavior of crashing the process when an
    // unobserved exception is detected. On .NET Core+ this is already the default, but
    // calling it explicitly is documented behavior — we want the runtime to know the
    // exception was handled here, not left in a "needs attention" state.
    args.SetObserved();
#pragma warning disable CA1848  // LoggerMessage source-gen — diagnostic path, low frequency
    _logger.LogError(
      args.Exception,
      "UnobservedTaskException captured — a fire-and-forget Task threw an exception that escaped its registered catch. Inspect the stack to identify the abandoned operation.");
#pragma warning restore CA1848
  }

  private void _onFirstChanceException(FirstChanceExceptionEventArgs args) {
    // FirstChanceException fires for EVERY exception including caught ones — very noisy.
    // Filter low-signal exception types (cancellation, expected-not-found patterns) so the
    // signal-to-noise stays usable in a diagnostic deploy. Operators raise the level via
    // options to see specific cases.
    var ex = args.Exception;
    if (ex is OperationCanceledException) {
      return;
    }
    if (!_options.IncludeExceptionTypeInFirstChanceLog(ex.GetType().FullName ?? string.Empty)) {
      return;
    }
    if (!_logger.IsEnabled(LogLevel.Debug)) {
      return;
    }
#pragma warning disable CA1848
    _logger.LogDebug(
      ex,
      "FirstChanceException: {ExceptionType} — {Message}",
      ex.GetType().FullName, ex.Message);
#pragma warning restore CA1848
  }

  /// <inheritdoc/>
  public void Dispose() {
    if (_disposed) { return; }
    _disposed = true;
    lock (_gate) {
      if (_registered != this) { return; }
      TaskScheduler.UnobservedTaskException -= _unobservedHandler;
      if (_firstChanceHandler is not null) {
        AppDomain.CurrentDomain.FirstChanceException -= _firstChanceHandler;
      }
      _registered = null;
    }
  }
}

/// <summary>Configuration for <see cref="UnobservedExceptionDiagnostics"/>.</summary>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/UnobservedExceptionDiagnosticsTests.cs:FirstChanceAllowList_NonMatchingType_IsExcludedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Observability/UnobservedExceptionDiagnosticsTests.cs:FirstChanceAllowList_NullOrEmpty_MatchesAllTypesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Observability/UnobservedExceptionDiagnosticsTests.cs:FirstChanceException_EnabledWithMatchingAllowList_LogsAtDebugAsync</tests>
public sealed class UnobservedExceptionDiagnosticsOptions {
  /// <summary>
  /// When true, subscribes <see cref="AppDomain.FirstChanceException"/> and logs at Debug.
  /// Default false — first-chance is high-volume and only useful for short diagnostic deploys.
  /// </summary>
  public bool EnableFirstChanceExceptionLogging { get; set; }

  /// <summary>
  /// Optional allow-list of exception type full names to log under first-chance. When null
  /// or empty, all non-OCE exceptions are logged. Use to narrow a diagnostic deploy to one
  /// suspected exception family.
  /// </summary>
  public IReadOnlyList<string>? FirstChanceExceptionTypeAllowList { get; set; }

  internal bool IncludeExceptionTypeInFirstChanceLog(string typeName) {
    var allow = FirstChanceExceptionTypeAllowList;
    if (allow is null || allow.Count == 0) { return true; }
    for (var i = 0; i < allow.Count; i++) {
      if (string.Equals(allow[i], typeName, StringComparison.Ordinal)) { return true; }
    }
    return false;
  }
}
