// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>A base event whose member gained a default after events were stored without it.</summary>
public abstract class MemberInitializerBaseEvent : IEvent {
  /// <summary>The stream.</summary>
  [StreamId]
  public Guid StreamId { get; init; }

  /// <summary>Added later, with a default.</summary>
  public string Source { get; init; } = "unknown";
}

/// <summary>An event whose members gained defaults after events were stored without them.</summary>
public sealed class MemberInitializerEvent : MemberInitializerBaseEvent {
  /// <summary>Present from the start.</summary>
  public IReadOnlyList<Guid> ItemIds { get; init; } = [];

  /// <summary>Added later: an empty list, not null.</summary>
  public IReadOnlyList<Guid> ExcludedIds { get; init; } = [];

  /// <summary>Added later: on unless a document says otherwise.</summary>
  public bool NotifyOnCreate { get; init; } = true;

  /// <summary>Added later, with a setter rather than an init accessor.</summary>
  public int Limit { get; set; } = 25;

  /// <summary>No default: a missing key reads as the type's default.</summary>
  public string? Note { get; init; }
}

/// <summary>A positional record with a further member that has a default.</summary>
/// <param name="OrderId">The stream.</param>
public sealed record MemberInitializerPositionalEvent([property: StreamId] Guid OrderId) : IEvent {
  /// <summary>Added later, with a default.</summary>
  public string Currency { get; init; } = "USD";
}

/// <summary>A command with a required member beside a member with a default.</summary>
public sealed class MemberInitializerRequiredCommand : ICommand {
  /// <summary>Required.</summary>
  public required string Name { get; init; }

  /// <summary>Added later, with a default.</summary>
  public int Attempts { get; init; } = 3;
}

/// <summary>
/// Issue #2374: a member's initializer survives a document that has no key for it. Every event stored before the
/// member existed has no key for it, so the declared default is what a replay or rebuild must read, not the type's
/// default.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/MessageJsonContextGenerator.cs</code-under-test>
/// <docs>fundamentals/events/event-upcasting#a-new-field-with-a-fixed-default</docs>
[Category("Serialization")]
public class MemberInitializerReadTests {
  private static readonly Guid _streamId = new("0f8c2a4e-2b7d-4c1a-9e5f-6a3b8d1c4e72");
  private static readonly Guid _itemId = new("5d1e9b3a-7c2f-4a8e-b6d4-1f0a9c3e5b27");

  private static T _read<T>(string json) =>
    JsonSerializer.Deserialize<T>(json, JsonContextRegistry.CreateCombinedOptions())!;

  [Test]
  public async Task AnEventStoredBeforeAMemberExisted_ReadsTheMembersDeclaredDefaultAsync() {
    var read = _read<MemberInitializerEvent>($$"""{"StreamId":"{{_streamId}}","ItemIds":["{{_itemId}}"]}""");

    await Assert.That(read.ExcludedIds).IsNotNull()
      .Because("a list declared = [] reads empty when the document has no key for it, not null");
    await Assert.That(read.ExcludedIds.Count).IsEqualTo(0);
    await Assert.That(read.NotifyOnCreate).IsTrue().Because("a bool declared = true stays true when its key is missing");
    await Assert.That(read.Limit).IsEqualTo(25).Because("a settable member keeps its default too");
    await Assert.That(read.Source).IsEqualTo("unknown").Because("an inherited member keeps the default its base declares");
    await Assert.That(read.Note).IsNull();
    await Assert.That(read.StreamId).IsEqualTo(_streamId);
    await Assert.That(read.ItemIds.Single()).IsEqualTo(_itemId);
  }

  [Test]
  public async Task AMemberTheDocumentHolds_ReadsTheDocumentsValueAsync() {
    var read = _read<MemberInitializerEvent>($$"""
      {"StreamId":"{{_streamId}}","ExcludedIds":["{{_itemId}}"],"NotifyOnCreate":false,"Limit":0,"Source":"import","Note":"n"}
      """);

    await Assert.That(read.ExcludedIds.Single()).IsEqualTo(_itemId);
    await Assert.That(read.NotifyOnCreate).IsFalse().Because("a value the document holds wins over the default");
    await Assert.That(read.Limit).IsEqualTo(0);
    await Assert.That(read.Source).IsEqualTo("import");
    await Assert.That(read.Note).IsEqualTo("n");
  }

  [Test]
  public async Task AnEvent_RoundTripsAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var written = new MemberInitializerEvent {
      StreamId = _streamId,
      ItemIds = [_itemId],
      ExcludedIds = [_itemId],
      NotifyOnCreate = false,
      Limit = 7,
      Source = "s",
    };

    var read = JsonSerializer.Deserialize<MemberInitializerEvent>(JsonSerializer.Serialize(written, options), options)!;

    await Assert.That(read.ExcludedIds.Single()).IsEqualTo(_itemId);
    await Assert.That(read.NotifyOnCreate).IsFalse();
    await Assert.That(read.Limit).IsEqualTo(7);
    await Assert.That(read.Source).IsEqualTo("s");
    await Assert.That(read.StreamId).IsEqualTo(_streamId);
  }

  [Test]
  public async Task APositionalRecord_KeepsTheDefaultOfAMemberOutsideItsConstructorAsync() {
    var read = _read<MemberInitializerPositionalEvent>($$"""{"OrderId":"{{_streamId}}"}""");

    await Assert.That(read.OrderId).IsEqualTo(_streamId);
    await Assert.That(read.Currency).IsEqualTo("USD");
  }

  [Test]
  public async Task ARequiredMember_IsStillRead_AndTheMemberBesideItKeepsItsDefaultAsync() {
    var read = _read<MemberInitializerRequiredCommand>("""{"Name":"n"}""");

    await Assert.That(read.Name).IsEqualTo("n");
    await Assert.That(read.Attempts).IsEqualTo(3);
  }
}
