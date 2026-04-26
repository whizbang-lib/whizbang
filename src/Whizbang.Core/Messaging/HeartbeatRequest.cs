using System.Text.Json;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Parameters for <see cref="IWorkCoordinator.RecordHeartbeatAsync"/>.
/// Decoupled from the polling-loop call so the heartbeat timer can fire on its own
/// cadence (5 s default) independent of work claim cadence.
/// </summary>
/// <param name="InstanceId">This service instance's identity.</param>
/// <param name="ServiceName">Service name (used for diagnostics and cross-instance grouping).</param>
/// <param name="HostName">Pod / host name.</param>
/// <param name="ProcessId">OS process id.</param>
/// <param name="Metadata">Optional metadata blob; defaults to empty object.</param>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public sealed record HeartbeatRequest(
  Guid InstanceId,
  string ServiceName,
  string HostName,
  int ProcessId,
  JsonElement? Metadata = null);
