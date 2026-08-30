namespace Whizbang.Core.Observability;

/// <summary>
/// An identity for a host that genuinely has none, reporting <see cref="ServiceInstanceInfo.Unknown"/>.
/// </summary>
/// <remarks>
/// <para>
/// "This host has no identity" is a real state with real behavior attached: gates that cannot
/// attribute a message fail open rather than discard it, and records stamp an explicitly unknown
/// writer. That state used to be expressed by passing null, which made it indistinguishable from
/// a caller who simply forgot, and forgetting was the far more common case.
/// </para>
/// <para>
/// Expressing it as a value instead keeps the behavior reachable while making the omission
/// impossible: a host without identity says so, and a host that meant to supply one cannot silently
/// fail to. This type is deliberately not registered as a default. Registering it would let a real
/// composition quietly run anonymous, which is the outcome the whole change exists to prevent; it
/// is for callers that construct these types directly and have no identity to give.
/// </para>
/// </remarks>
/// <docs>operations/dependency-injection/injectable-services</docs>
/// <tests>tests/Whizbang.Core.Tests/DependencyInjection/InstanceProviderWiringTests.cs</tests>
public sealed class UnknownServiceInstanceProvider : IServiceInstanceProvider {

  /// <summary>A shared instance; the value is immutable.</summary>
  public static readonly UnknownServiceInstanceProvider Instance = new();

  /// <inheritdoc />
  public Guid InstanceId => ServiceInstanceInfo.Unknown.InstanceId;

  /// <inheritdoc />
  public string ServiceName => ServiceInstanceInfo.Unknown.ServiceName;

  /// <inheritdoc />
  public string HostName => ServiceInstanceInfo.Unknown.HostName;

  /// <inheritdoc />
  public int ProcessId => ServiceInstanceInfo.Unknown.ProcessId;

  /// <inheritdoc />
  public ServiceInstanceInfo ToInfo() => ServiceInstanceInfo.Unknown;
}
