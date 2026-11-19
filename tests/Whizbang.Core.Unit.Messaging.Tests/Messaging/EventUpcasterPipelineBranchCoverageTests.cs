// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Branch coverage for <see cref="EventUpcasterPipeline.ExtraInputTypeNamesFor"/> when no registered
/// upcaster changes a type (a re-key upcaster keeps the type it reads): there are no foreign inputs
/// to widen a rebuild's stream scan with, so the answer is empty whatever is requested.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/EventUpcasterPipeline.cs</code-under-test>
public class EventUpcasterPipelineBranchCoverageTests {
#pragma warning disable WHIZ009
  public record RekeyedEvent : IEvent {
    [StreamId] public Guid StreamId { get; set; }
  }
#pragma warning restore WHIZ009

  /// <summary>Same type in, same type out: declares no source or target types.</summary>
  private sealed class RekeyUpcaster : IEventUpcaster {
    public bool CanUpcast(IEvent storedEvent) => storedEvent is RekeyedEvent;
    public IEvent Upcast(IEvent storedEvent) => new RekeyedEvent { StreamId = ((RekeyedEvent)storedEvent).StreamId };
  }

  [Test]
  public async Task ExtraInputTypeNamesFor_NoTypeChangingUpcaster_ReturnsEmptyAsync() {
    var pipeline = new EventUpcasterPipeline([new RekeyUpcaster()]);

    var names = pipeline.ExtraInputTypeNamesFor([TypeNameFormatter.Format(typeof(RekeyedEvent))]);

    await Assert.That(pipeline.HasAny).IsTrue()
      .Because("the upcaster is registered; it simply changes no type");
    await Assert.That(pipeline.HasTypeChanges).IsFalse();
    await Assert.That(names.Count).IsEqualTo(0)
      .Because("a re-key upcaster reads the type the perspective already subscribes to, so nothing extra is read");
  }
}
