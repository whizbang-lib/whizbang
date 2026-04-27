using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// No-op implementation of <see cref="IDatabaseReadinessCheck"/> that always reports ready.
/// Compatibility shim for fixtures still constructing legacy types.
/// </summary>
public sealed class DefaultDatabaseReadinessCheck : IDatabaseReadinessCheck {
  /// <inheritdoc />
  public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
}
