using System;
using System.Threading.Tasks;

namespace Whizbang.Core.Transports;

/// <summary>
/// Represents an active subscription to a transport.
/// Allows controlling the subscription (pause, resume, dispose).
/// </summary>
public interface ISubscription : IDisposable {
  /// <summary>
  /// Gets whether the subscription is currently active.
  /// When paused, the subscription will not receive new messages.
  /// </summary>
  bool IsActive { get; }

  /// <summary>
  /// Pauses the subscription.
  /// No new messages will be delivered until Resume is called.
  /// Messages may be buffered by the transport depending on implementation.
  /// </summary>
  Task PauseAsync();

  /// <summary>
  /// Resumes a paused subscription.
  /// Messages will begin being delivered again.
  /// </summary>
  Task ResumeAsync();
}
