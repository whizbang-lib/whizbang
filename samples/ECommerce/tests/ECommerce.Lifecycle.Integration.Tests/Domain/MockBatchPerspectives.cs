using Whizbang;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace ECommerce.Lifecycle.Integration.Tests.Domain;

// 5 mock perspectives handle MockBatchTestEvent, forcing multiple batch cycles
// when PerspectiveBatchSize=1.

[WhizbangSerializable]
public record MockBatchModelA {
  [StreamId] public Guid Id { get; init; }
  public DateTimeOffset CreatedAt { get; init; }
}

[WhizbangSerializable]
public record MockBatchModelB {
  [StreamId] public Guid Id { get; init; }
  public DateTimeOffset CreatedAt { get; init; }
}

[WhizbangSerializable]
public record MockBatchModelC {
  [StreamId] public Guid Id { get; init; }
  public DateTimeOffset CreatedAt { get; init; }
}

[WhizbangSerializable]
public record MockBatchModelD {
  [StreamId] public Guid Id { get; init; }
  public DateTimeOffset CreatedAt { get; init; }
}

[WhizbangSerializable]
public record MockBatchModelE {
  [StreamId] public Guid Id { get; init; }
  public DateTimeOffset CreatedAt { get; init; }
}

public class MockBatchPerspectiveA : IPerspectiveFor<MockBatchModelA, MockBatchTestEvent, MockBatchNoiseEvent> {
  public MockBatchModelA Apply(MockBatchModelA current, MockBatchTestEvent @event) =>
    current with { Id = @event.StreamId, CreatedAt = @event.CreatedAt };
  public MockBatchModelA Apply(MockBatchModelA current, MockBatchNoiseEvent @event) => current;
}

public class MockBatchPerspectiveB : IPerspectiveFor<MockBatchModelB, MockBatchTestEvent, MockBatchNoiseEvent> {
  public MockBatchModelB Apply(MockBatchModelB current, MockBatchTestEvent @event) =>
    current with { Id = @event.StreamId, CreatedAt = @event.CreatedAt };
  public MockBatchModelB Apply(MockBatchModelB current, MockBatchNoiseEvent @event) => current;
}

public class MockBatchPerspectiveC : IPerspectiveFor<MockBatchModelC, MockBatchTestEvent, MockBatchNoiseEvent> {
  public MockBatchModelC Apply(MockBatchModelC current, MockBatchTestEvent @event) =>
    current with { Id = @event.StreamId, CreatedAt = @event.CreatedAt };
  public MockBatchModelC Apply(MockBatchModelC current, MockBatchNoiseEvent @event) => current;
}

public class MockBatchPerspectiveD : IPerspectiveFor<MockBatchModelD, MockBatchTestEvent, MockBatchNoiseEvent> {
  public MockBatchModelD Apply(MockBatchModelD current, MockBatchTestEvent @event) =>
    current with { Id = @event.StreamId, CreatedAt = @event.CreatedAt };
  public MockBatchModelD Apply(MockBatchModelD current, MockBatchNoiseEvent @event) => current;
}

public class MockBatchPerspectiveE : IPerspectiveFor<MockBatchModelE, MockBatchTestEvent, MockBatchNoiseEvent> {
  public MockBatchModelE Apply(MockBatchModelE current, MockBatchTestEvent @event) =>
    current with { Id = @event.StreamId, CreatedAt = @event.CreatedAt };
  public MockBatchModelE Apply(MockBatchModelE current, MockBatchNoiseEvent @event) => current;
}
