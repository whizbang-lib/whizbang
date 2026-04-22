using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Small indirection that lets workers call into <see cref="IChaosInjector"/> checkpoints
/// without each worker re-checking the options flag. Workers resolve this from the scope
/// once in their constructor and call <see cref="BeforeCheckpointAsync"/> at named points.
/// </summary>
/// <remarks>
/// When chaos hooks are disabled (the default in production) this is a no-op: the boolean
/// check on the first line short-circuits before we touch the (possibly null) injector.
/// When enabled, a missing injector still short-circuits — you only pay real cost when a
/// test has actually registered an <see cref="IChaosInjector"/>.
/// </remarks>
/// <docs>operations/testing/chaos-injection</docs>
public sealed class ChaosInjectorInvoker(
  IOptions<Configuration.WhizbangOptions>? options,
  IChaosInjector? injector = null) {
  private readonly IChaosInjector? _injector = injector;
  private readonly bool _enabled = options?.Value?.Guardrails.EnableChaosHooks ?? false;

  /// <summary>Whether chaos hooks are enabled AND an injector is registered.</summary>
  public bool IsActive => _enabled && _injector is not null;

  /// <summary>
  /// Invoke the injector at the named checkpoint if both the options flag is on and an
  /// injector is registered. Returns <see cref="ValueTask.CompletedTask"/> otherwise.
  /// </summary>
  public ValueTask BeforeCheckpointAsync(string checkpoint, object? payload, CancellationToken cancellationToken) {
    if (!_enabled || _injector is null) {
      return ValueTask.CompletedTask;
    }
    return _injector.BeforeCheckpointAsync(checkpoint, payload, cancellationToken);
  }
}
