using Whizbang.Core;

namespace Whizbang.Testing.Tests.TestSupport;

/// <summary>
/// Test event used to drive lifecycle awaiters and receptors.
/// </summary>
public sealed record TestEvent(string Name) : IEvent;

/// <summary>
/// Test command used to drive lifecycle awaiters.
/// </summary>
public sealed record TestCommand(string Name) : ICommand;

/// <summary>
/// Simple payload with string content for transport tests.
/// </summary>
public sealed class TestPayload {
  public required string Content { get; init; }
}

/// <summary>
/// A second payload type used to exercise type-mismatch branches in transport helpers.
/// </summary>
public sealed class OtherPayload {
  public required string Content { get; init; }
}
