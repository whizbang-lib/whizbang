using System;

namespace Whizbang.Core.Transports;

/// <summary>
/// <tests>tests/Whizbang.Transports.Tests/TransportCapabilitiesTests.cs:TransportCapabilities_HasNoneValueAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportCapabilitiesTests.cs:TransportCapabilities_HasRequestResponseAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportCapabilitiesTests.cs:TransportCapabilities_HasPublishSubscribeAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportCapabilitiesTests.cs:TransportCapabilities_HasStreamingAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportCapabilitiesTests.cs:TransportCapabilities_HasReliableAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportCapabilitiesTests.cs:TransportCapabilities_HasOrderedAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportCapabilitiesTests.cs:TransportCapabilities_HasExactlyOnceAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportCapabilitiesTests.cs:TransportCapabilities_CanCombineFlagsAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportCapabilitiesTests.cs:TransportCapabilities_AllFlag_ContainsAllCapabilitiesAsync</tests>
/// Defines the capabilities that a transport can support.
/// These are flags that can be combined to describe what a transport is capable of.
/// </summary>
[Flags]
public enum TransportCapabilities {
  /// <summary>
  /// No capabilities.
  /// </summary>
  None = 0,

  /// <summary>
  /// Supports request/response pattern (Send/Receive).
  /// </summary>
  RequestResponse = 1 << 0,

  /// <summary>
  /// Supports publish/subscribe pattern.
  /// </summary>
  PublishSubscribe = 1 << 1,

  /// <summary>
  /// Supports streaming messages (IAsyncEnumerable).
  /// </summary>
  Streaming = 1 << 2,

  /// <summary>
  /// Provides reliable delivery (at-least-once semantics).
  /// Messages will be retried until acknowledged.
  /// </summary>
  Reliable = 1 << 3,

  /// <summary>
  /// Guarantees message ordering within a stream/partition.
  /// </summary>
  Ordered = 1 << 4,

  /// <summary>
  /// Provides exactly-once delivery semantics.
  /// Requires Inbox/Outbox pattern or equivalent deduplication.
  /// </summary>
  ExactlyOnce = 1 << 5,

  /// <summary>
  /// All capabilities combined.
  /// </summary>
  All = RequestResponse | PublishSubscribe | Streaming | Reliable | Ordered | ExactlyOnce
}
