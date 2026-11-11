// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests;

/// <summary>A strong id used as a message's stream id (#2072).</summary>
[WhizbangId]
public readonly partial struct RoutedThingId;

#pragma warning disable WHIZ015 // The generic base is never sent; only the messages that close it are
/// <summary>A generic base command whose <c>[StreamId]</c> is a type parameter.</summary>
public abstract class RoutedBaseCommand<TStreamId> : ICommand where TStreamId : struct {
  [StreamId]
  public TStreamId StreamId { get; set; }
}
#pragma warning restore WHIZ015

#pragma warning disable WHIZ015 // The generic base is never sent; only the messages that close it are
/// <summary>A generic base event whose <c>[StreamId]</c> is a type parameter.</summary>
public abstract class RoutedBaseEvent<TStreamId> : IEvent where TStreamId : struct {
  [StreamId]
  public TStreamId StreamId { get; set; }
}
#pragma warning restore WHIZ015

/// <summary>A command that closes the generic base with a strong id.</summary>
public class CreateRoutedThing : RoutedBaseCommand<RoutedThingId> {
  public string Name { get; set; } = "";
}

/// <summary>An event that closes the generic base with a strong id.</summary>
public class RoutedThingCreated : RoutedBaseEvent<RoutedThingId> {
  public string Name { get; set; } = "";
}

/// <summary>A command with a strong-id property named like a stream id but no <c>[StreamId]</c>.</summary>
public class UnmarkedStrongIdCommand : ICommand {
  public RoutedThingId StreamId { get; set; }
}

#pragma warning disable WHIZ009 // Intentionally missing [StreamId]: no stream may be inferred from the strong id
/// <summary>An event with a strong-id property named like a stream id but no <c>[StreamId]</c>.</summary>
public class UnmarkedStrongIdEvent : IEvent {
  public RoutedThingId StreamId { get; set; }
}

#pragma warning disable WHIZ015 // The generic base is never sent; only the messages that close it are
/// <summary>A generic base event whose strong-id property carries no <c>[StreamId]</c>.</summary>
public abstract class UnmarkedBaseEvent<TId> : IEvent where TId : struct {
  public TId StreamId { get; set; }
}
#pragma warning restore WHIZ015

/// <summary>An event that closes the unmarked generic base with a strong id.</summary>
public class UnmarkedClosedEvent : UnmarkedBaseEvent<RoutedThingId>;
#pragma warning restore WHIZ009

#pragma warning disable WHIZ015 // The generic base is never sent; only the messages that close it are
/// <summary>A generic base command whose strong-id property carries no <c>[StreamId]</c>.</summary>
public abstract class UnmarkedBaseCommand<TId> : ICommand where TId : struct {
  public TId StreamId { get; set; }
}
#pragma warning restore WHIZ015

/// <summary>A command that closes the unmarked generic base with a strong id.</summary>
public class UnmarkedClosedCommand : UnmarkedBaseCommand<RoutedThingId>;

/// <summary>
/// Issue #2072, at run time: the stream-id extractor this project's generators emit routes a message that closes a
/// generic base with a strong id by that id's Guid, and infers no stream from a strong-id property that carries no
/// <c>[StreamId]</c>, so the attribute stays the only way a stream is chosen.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/StreamIdGenerator.cs</code-under-test>
[Category("StreamId")]
public class StrongIdStreamIdRoutingTests {
  private static readonly GeneratedStreamIdExtractor _extractor = new();

  [Test]
  public async Task ClosedGenericCommand_ExtractStreamId_ReturnsTheStrongIdsGuidAsync() {
    var id = RoutedThingId.New();
    var command = new CreateRoutedThing { StreamId = id, Name = "widget" };

    var streamId = _extractor.ExtractStreamId(command, command.GetType());

    await Assert.That(streamId).IsEqualTo(id.Value)
      .Because("the command closes BaseCommand<TStreamId> with a strong id, and its stream is that id's Guid");
  }

  [Test]
  public async Task ClosedGenericEvent_ExtractStreamId_ReturnsTheStrongIdsGuidAsync() {
    var id = RoutedThingId.New();
    var @event = new RoutedThingCreated { StreamId = id, Name = "widget" };

    var streamId = _extractor.ExtractStreamId(@event, @event.GetType());

    await Assert.That(streamId).IsEqualTo(id.Value)
      .Because("the event closes BaseEvent<TStreamId> with a strong id, and its stream is that id's Guid");
  }

  [Test]
  public async Task StrongIdWithoutStreamIdAttribute_OnACommand_InfersNoStreamAsync() {
    var command = new UnmarkedStrongIdCommand { StreamId = RoutedThingId.New() };

    await Assert.That(_extractor.ExtractStreamId(command, command.GetType())).IsNull()
      .Because("a property's type or name never chooses a stream; only [StreamId] does");
  }

  [Test]
  public async Task StrongIdWithoutStreamIdAttribute_OnAnEvent_InfersNoStreamAsync() {
    var @event = new UnmarkedStrongIdEvent { StreamId = RoutedThingId.New() };

    await Assert.That(_extractor.ExtractStreamId(@event, @event.GetType())).IsNull()
      .Because("a property's type or name never chooses a stream; only [StreamId] does");
  }

  [Test]
  public async Task StrongIdWithoutStreamIdAttribute_ThroughAGenericBaseCommand_InfersNoStreamAsync() {
    var command = new UnmarkedClosedCommand { StreamId = RoutedThingId.New() };

    await Assert.That(_extractor.ExtractStreamId(command, command.GetType())).IsNull()
      .Because("closing a generic base with a strong id must not turn an unmarked property into a stream id");
  }

  [Test]
  public async Task StrongIdWithoutStreamIdAttribute_ThroughAGenericBaseEvent_InfersNoStreamAsync() {
    var @event = new UnmarkedClosedEvent { StreamId = RoutedThingId.New() };

    await Assert.That(_extractor.ExtractStreamId(@event, @event.GetType())).IsNull()
      .Because("closing a generic base with a strong id must not turn an unmarked property into a stream id");
  }
}
