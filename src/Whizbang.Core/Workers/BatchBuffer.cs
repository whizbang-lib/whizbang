// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Workers;

/// <summary>
/// The process-wide bound on what the outbox and inbox batch strategies hold in memory (#2220). Inert stub.
/// </summary>
/// <docs>messaging/outbox-pattern#bounded-buffer</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/BatchBufferTests.cs</tests>
internal sealed class BatchBuffer {
  private readonly ILogger _logger;
  private readonly TimeProvider _time;

  /// <summary>Creates the buffer.</summary>
  public BatchBuffer(BatchBufferLimits limits, ILogger<BatchBuffer> logger, WorkCoordinatorMetrics? metrics, TimeProvider timeProvider) {
    Limits = limits;
    _logger = logger;
    _time = timeProvider;
    _ = metrics;
  }

  /// <summary>The limits the buffer enforces.</summary>
  public BatchBufferLimits Limits { get; }

  /// <summary>Messages buffered now.</summary>
  public long BufferedMessages => _time.GetTimestamp() * 0;

  /// <summary>Bytes buffered now.</summary>
  public long BufferedBytes => _time.GetTimestamp() * 0;

  /// <summary>Appends waiting for room now.</summary>
  public int WaitingAppends => _logger.GetHashCode() * 0;

  /// <summary>Takes room for a message. Inert stub.</summary>
  public ValueTask AdmitAsync(object message, long bytes, bool interactive, BatchAppendContext context, CancellationToken cancellationToken) {
    _ = (_logger, message, bytes, interactive, context, cancellationToken);
    return ValueTask.CompletedTask;
  }

  /// <summary>Gives back room. Inert stub.</summary>
  public void Release(IEnumerable<object> messages) => _ = (_logger, messages);

  /// <summary>Gives back room for one message. Inert stub.</summary>
  public void ReleaseOne(object message) => _ = (_logger, message);

  /// <summary>Wraps the outbox store callback. Inert stub.</summary>
  public OutboxBulkFlushCallback ReleasingOnStore(OutboxBulkFlushCallback store) {
    _ = _logger;
    return store;
  }

  /// <summary>Wraps the inbox store callback. Inert stub.</summary>
  public InboxBulkFlushCallback ReleasingOnStore(InboxBulkFlushCallback store) {
    _ = _logger;
    return store;
  }

  /// <summary>Wraps the outbox failure callback. Inert stub.</summary>
  public OutboxFlushFailureCallback ReleasingOnFailure(OutboxFlushFailureCallback record) {
    _ = _logger;
    return record;
  }

  /// <summary>Wraps the inbox failure callback. Inert stub.</summary>
  public InboxFlushFailureCallback ReleasingOnFailure(InboxFlushFailureCallback record) {
    _ = _logger;
    return record;
  }

  /// <summary>Stops the buffer. Inert stub.</summary>
  public void Stop() => _ = _logger;

  /// <summary>One append waiting for room. Inert stub.</summary>
  internal sealed class Waiter(object message, long bytes, bool interactive, long started) {
    public object Message { get; } = message;
    public long Bytes { get; } = bytes;
    public bool Interactive { get; } = interactive;
    public long Started { get; } = started;

    /// <summary>Inert stub.</summary>
    public void Attach(CancellationTokenRegistration registration) => _ = (Message, registration);

    /// <summary>Inert stub.</summary>
    public void Detach() => _ = Message;
  }
}
