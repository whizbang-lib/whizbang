using System.Collections.Concurrent;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Thread-safe implementation of <see cref="ILifecycleReceptorRegistry"/> using concurrent collections.
/// </summary>
/// <remarks>
/// This implementation is safe for concurrent registration and unregistration from multiple threads.
/// Primarily used in integration tests where receptors may be registered/unregistered while
/// the application is processing messages.
/// </remarks>
/// <docs>testing/lifecycle-synchronization</docs>
public sealed class DefaultLifecycleReceptorRegistry : ILifecycleReceptorRegistry {
  // Key: (MessageType, LifecycleStage), Value: List of receptor instances
  private readonly ConcurrentDictionary<(Type MessageType, LifecycleStage Stage), List<object>> _receptors = new();

  /// <inheritdoc/>
  public void Register<TMessage>(object receptor, LifecycleStage stage) where TMessage : IMessage {
    ArgumentNullException.ThrowIfNull(receptor);

    var key = (typeof(TMessage), stage);

    _receptors.AddOrUpdate(
      key,
      _ => new List<object> { receptor },
      (_, existingList) => {
        lock (existingList) {
          existingList.Add(receptor);
          return existingList;
        }
      }
    );
  }

  /// <inheritdoc/>
  public bool Unregister<TMessage>(object receptor, LifecycleStage stage) where TMessage : IMessage {
    ArgumentNullException.ThrowIfNull(receptor);

    var key = (typeof(TMessage), stage);

    if (_receptors.TryGetValue(key, out var receptorList)) {
      lock (receptorList) {
        return receptorList.Remove(receptor);
      }
    }

    return false;
  }

  /// <inheritdoc/>
  public IReadOnlyList<object> GetReceptors(Type messageType, LifecycleStage stage) {
    ArgumentNullException.ThrowIfNull(messageType);

    var key = (messageType, stage);

    if (_receptors.TryGetValue(key, out var receptorList)) {
      lock (receptorList) {
        // Return a copy to avoid concurrent modification issues
        return receptorList.ToList();
      }
    }

    return Array.Empty<object>();
  }
}
