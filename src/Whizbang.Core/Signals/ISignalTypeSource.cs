namespace Whizbang.Core.Signals;

/// <summary>
/// A per-assembly source of signal-type metadata. Each assembly that declares <see cref="ISignal"/>
/// types gets a generated implementation registered into <see cref="SignalTypeRegistry"/> via a
/// module initializer, so the running host sees the combined union across the dependency chain.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public interface ISignalTypeSource {
  /// <summary>The signal types declared in this source's assembly.</summary>
  IReadOnlyList<SignalTypeEntry> GetSignalTypes();
}
