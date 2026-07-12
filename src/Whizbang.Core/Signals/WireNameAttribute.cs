namespace Whizbang.Core.Signals;

/// <summary>
/// Overrides the default wire-name (the fully-qualified type name) that
/// <see cref="SignalTypeRegistry"/> exposes for an <see cref="ISignal"/> type. The wire-name is
/// what the Postgres transport puts on the <c>pg_notify</c> payload and matches against on receive.
/// </summary>
/// <remarks>
/// <para>
/// The default is fine when a signal has no cross-service wire compatibility constraints — the
/// FQ type name is unique and stable. Use this attribute when a signal must interoperate with an
/// existing wire-format, e.g. the work-signal payloads (<c>"outbox"</c>, <c>"inbox"</c>,
/// <c>"perspective"</c>) that Whizbang's SQL store procs emit today.
/// </para>
/// <para>
/// AOT-safe: the wire-name is read at compile time by the signal-registry generator and baked
/// into the generated <c>ISignalTypeSource</c>; no reflection at runtime.
/// </para>
/// </remarks>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class WireNameAttribute(string wireName) : Attribute {
  /// <summary>The wire-name emitted on the NOTIFY payload.</summary>
  public string WireName { get; } = wireName ?? throw new ArgumentNullException(nameof(wireName));
}
