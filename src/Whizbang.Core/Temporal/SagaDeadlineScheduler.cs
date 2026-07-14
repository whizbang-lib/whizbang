using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Whizbang.Core.Temporal;

/// <summary>
/// Default <see cref="ISagaDeadlineScheduler"/> — a thin, provider-agnostic layer over
/// <see cref="IScheduleManager"/>. A deadline is a keyed one-shot schedule whose id is derived
/// deterministically from (saga stream, deadline name), so set-again is idempotent and cancel needs no
/// caller-side bookkeeping. Nothing here is timing logic: the engine does the firing.
/// </summary>
/// <docs>fundamentals/temporal/saga-deadlines</docs>
public sealed class SagaDeadlineScheduler : ISagaDeadlineScheduler {
  private readonly IScheduleManager _manager;

  /// <summary>Constructor.</summary>
  public SagaDeadlineScheduler(IScheduleManager manager) =>
    _manager = manager ?? throw new ArgumentNullException(nameof(manager));

  /// <inheritdoc />
  public Guid DeadlineScheduleId(Guid sagaStreamId, string deadlineName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(deadlineName);
    // Stable derivation (not a security boundary): first 16 bytes of SHA-256 over the scoped key.
    var bytes = Encoding.UTF8.GetBytes(_key(sagaStreamId, deadlineName));
    var hash = SHA256.HashData(bytes);
    return new Guid(hash.AsSpan(0, 16));
  }

  /// <inheritdoc />
  public Task<ScheduleHandle> SetDeadlineAsync(
      Guid sagaStreamId,
      string deadlineName,
      DateTimeOffset at,
      string eventType,
      Guid authorityPrincipalId,
      string? authorityClaimsJson = null,
      string? eventDataJson = null,
      string? scopeJson = null,
      CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(deadlineName);
    ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

    // Keyed one-shot: create-or-update by key means re-arming moves the deadline instead of stacking
    // a second one, and it re-activates a previously cancelled deadline.
    return _manager.CreateAsync(new ScheduleDefinition {
      ScheduleId = DeadlineScheduleId(sagaStreamId, deadlineName),
      Key = _key(sagaStreamId, deadlineName),
      StreamId = sagaStreamId,
      Kind = RecurrenceKind.OneShot,
      StartAt = at,
      EventType = eventType,
      AuthorityPrincipalId = authorityPrincipalId,
      AuthorityClaimsJson = authorityClaimsJson,
      EventDataJson = eventDataJson,
      ScopeJson = scopeJson,
    }, cancellationToken);
  }

  /// <inheritdoc />
  public Task<bool> CancelDeadlineAsync(Guid sagaStreamId, string deadlineName, CancellationToken cancellationToken = default) =>
    _manager.CancelAsync(DeadlineScheduleId(sagaStreamId, deadlineName), expectedVersion: null, cancellationToken);

  private static string _key(Guid sagaStreamId, string deadlineName) =>
    string.Create(CultureInfo.InvariantCulture, $"saga:{sagaStreamId:N}:{deadlineName}");
}
