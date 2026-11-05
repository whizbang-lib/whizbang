// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Signals;

/// <summary>
/// A signal that carries a short text payload: the one fact its subscriber needs to act without a database read, such
/// as which role was released or which assignment version was published (#2046). Still a doorbell: the payload names
/// what changed, and the database stays the authority on what it now is.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public interface IPayloadSignal : ISignal {
  /// <summary>The payload the signal carries on the wire.</summary>
  string Payload { get; }
}

/// <summary>
/// A payload signal that a transport can rebuild from its payload, with no reflection: the type names its own factory.
/// </summary>
/// <typeparam name="TSelf">The signal type itself.</typeparam>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public interface IPayloadSignal<out TSelf> : IPayloadSignal where TSelf : IPayloadSignal<TSelf> {
  /// <summary>Rebuilds the signal from the payload it carried.</summary>
  static abstract TSelf FromPayload(string payload);
}
