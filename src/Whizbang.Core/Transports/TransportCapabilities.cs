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
  /// <tests>tests/Whizbang.Transports.Tests/TransportCapabilitiesTests.cs:TransportCapabilities_HasNoneValueAsync</tests>
  None = 0,

  /// <summary>
  /// Supports request/response pattern (Send/Receive).
  /// </summary>
  /// <tests>tests/Whizbang.Transports.Tests/TransportCapabilitiesTests.cs:TransportCapabilities_HasRequestResponseAsync</tests>
  RequestResponse = 1 << 0,

  /// <summary>
  /// Supports publish/subscribe pattern.
  /// </summary>
  /// <tests>tests/Whizbang.Transports.Tests/TransportCapabilitiesTests.cs:TransportCapabilities_HasPublishSubscribeAsync</tests>
  PublishSubscribe = 1 << 1,

  /// <summary>
  /// Supports streaming messages (IAsyncEnumerable).
  /// </summary>
  /// <tests>tests/Whizbang.Transports.Tests/TransportCapabilitiesTests.cs:TransportCapabilities_HasStreamingAsync</tests>
  Streaming = 1 << 2,

  /// <summary>
  /// Provides reliable delivery (at-least-once semantics).
  /// Messages will be retried until acknowledged.
  /// </summary>
  /// <tests>tests/Whizbang.Transports.Tests/TransportCapabilitiesTests.cs:TransportCapabilities_HasReliableAsync</tests>
  Reliable = 1 << 3,

  /// <summary>
  /// Guarantees message ordering within a stream/partition.
  /// </summary>
  /// <tests>tests/Whizbang.Transports.Tests/TransportCapabilitiesTests.cs:TransportCapabilities_HasOrderedAsync</tests>
  Ordered = 1 << 4,

  /// <summary>
  /// Provides exactly-once delivery semantics.
  /// Requires Inbox/Outbox pattern or equivalent deduplication.
  /// </summary>
  /// <tests>tests/Whizbang.Transports.Tests/TransportCapabilitiesTests.cs:TransportCapabilities_HasExactlyOnceAsync</tests>
  ExactlyOnce = 1 << 5,

  /// <summary>
  /// All capabilities combined.
  /// </summary>
  /// <tests>tests/Whizbang.Transports.Tests/TransportCapabilitiesTests.cs:TransportCapabilities_AllFlag_ContainsAllCapabilitiesAsync</tests>
  All = RequestResponse | PublishSubscribe | Streaming | Reliable | Ordered | ExactlyOnce
}
