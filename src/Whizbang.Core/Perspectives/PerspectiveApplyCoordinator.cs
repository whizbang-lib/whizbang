using System.Collections.Concurrent;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Default <see cref="IPerspectiveApplyCoordinator"/> backed by per-key
/// <see cref="SemaphoreSlim"/>s in a <see cref="ConcurrentDictionary{TKey, TValue}"/>.
/// </summary>
/// <remarks>
/// Allocations are lazy: a semaphore is created the first time a key is
/// acquired and is then reused for the lifetime of the process. Releases are
/// reentrancy-safe via the returned handle's <c>DisposeAsync</c>; the handle
/// itself is reusable only once (matches <see cref="IAsyncDisposable"/>'s
/// "dispose-then-discard" contract).
/// </remarks>
/// <docs>fundamentals/perspectives/rewind</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/RewindLiveApplyRaceTests.cs:Rewind_ConcurrentWithLiveApply_WithCoordinator_RetainsAllIncrementsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/RewindLiveApplyRaceTests.cs:Rewind_NoConcurrentLiveApply_DoesNotLoseIncrementsAsync</tests>
public sealed class PerspectiveApplyCoordinator : IPerspectiveApplyCoordinator {
  private readonly ConcurrentDictionary<(Guid streamId, string perspectiveName), SemaphoreSlim> _byKey = new();

  /// <inheritdoc />
  public async Task<IAsyncDisposable> AcquireAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(perspectiveName);
    var semaphore = _byKey.GetOrAdd((streamId, perspectiveName), static _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));
    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
    return new _Handle(semaphore);
  }

  private sealed class _Handle(SemaphoreSlim semaphore) : IAsyncDisposable {
    private int _disposed;
    public ValueTask DisposeAsync() {
      if (Interlocked.Exchange(ref _disposed, 1) == 0) {
        semaphore.Release();
      }
      return ValueTask.CompletedTask;
    }
  }
}
