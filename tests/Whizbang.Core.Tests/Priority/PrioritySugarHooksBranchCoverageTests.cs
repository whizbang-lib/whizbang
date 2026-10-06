// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Priority;
using Whizbang.Core.Tags;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Priority;

/// <summary>
/// Branch backfill for <see cref="TagDeclaredPriorityProducerHook"/>'s tag-to-type index: a tagged
/// type with no assembly-qualified name is keyed by its display name, and a type carrying two
/// declared tags takes the more urgent declaration.
/// </summary>
/// <remarks>
/// The tag registry is process-global, so the registry registered here (the same pattern as
/// <c>PriorityTagSurfaceTests</c>) only reports its registrations inside a flow that switched it on
/// through an <see cref="AsyncLocal{T}"/>. Every other test's flow sees an empty registry, so the
/// unusual types below can never reach another test's tag enumeration.
/// </remarks>
[Category("Unit")]
public class PrioritySugarHooksBranchCoverageTests {

  private const string URGENT_TAG = "branch-coverage-priority-urgent";
  private const string RELAXED_TAG = "branch-coverage-priority-relaxed";
  private const string NAMELESS_TAG = "branch-coverage-priority-nameless";

  private sealed record TwoTagEvent(string Id) : IEvent;

  // A generic type parameter: a Type whose AssemblyQualifiedName (and FullName) is null.
  private static readonly Type _namelessType = typeof(List<>).GetGenericArguments()[0];

  private static readonly AsyncLocal<bool> _active = new();

  private static readonly Lazy<bool> _registered = new(() => {
    MessageTagRegistry.Register(new FlowScopedRegistry());
    return true;
  });

  private sealed class FlowScopedRegistry : IMessageTagRegistry {
    private static readonly MessageTagRegistration[] _registrations = [
      _registration(typeof(TwoTagEvent), RELAXED_TAG),
      _registration(typeof(TwoTagEvent), URGENT_TAG),
      _registration(_namelessType, NAMELESS_TAG),
    ];

    public IEnumerable<MessageTagRegistration> GetTagsFor(Type messageType) =>
      _active.Value ? _registrations.Where(r => r.MessageType == messageType) : [];

    public IEnumerable<MessageTagRegistration> GetAllTags() => _active.Value ? _registrations : [];

    private static MessageTagRegistration _registration(Type messageType, string tag) => new() {
      MessageType = messageType,
      AttributeType = typeof(MessageTagAttribute),
      Tag = tag,
      PayloadBuilder = _ => JsonSerializer.SerializeToElement(new { }),
      AttributeFactory = () => throw new NotSupportedException("no attribute instance in this test"),
    };
  }

  private static PriorityDeclarationContext _declaration(string messageTypeName) =>
    new(new MessageEnvelope<string> {
      MessageId = MessageId.From((Guid)TrackedGuid.New()),
      Payload = "x",
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Both, Source = MessageSource.Local, HandlerName = "H" },
    }, messageTypeName, null, false, WorkPriority.UNDECLARED, WorkPriority.UNDECLARED);

  [Test]
  public async Task DeclarePriority_TypeWithTwoDeclaredTags_TakesTheMoreUrgentDeclarationAsync() {
    _ = _registered.Value;
    _active.Value = true;
    // Declared in both orders across the two tags, so the result cannot be "whichever came last".
    var tags = new TagOptions()
      .DeclarePriority(RELAXED_TAG, WorkPriority.BACKGROUND)
      .DeclarePriority(URGENT_TAG, WorkPriority.INTERACTIVE);
    var hook = new TagDeclaredPriorityProducerHook(tags);

    var declared = hook.DeclarePriority(_declaration(TypeNameFormatter.AssemblyQualifiedName(typeof(TwoTagEvent))));

    await Assert.That(declared).IsEqualTo(WorkPriority.INTERACTIVE)
      .Because("a type carrying two declared tags takes the more urgent (lower) declaration");
  }

  [Test]
  public async Task DeclarePriority_TaggedTypeWithNoAssemblyQualifiedName_IsKeyedByDisplayNameAsync() {
    _ = _registered.Value;
    _active.Value = true;
    var tags = new TagOptions().DeclarePriority(NAMELESS_TAG, WorkPriority.BACKGROUND);
    var hook = new TagDeclaredPriorityProducerHook(tags);

    var declared = hook.DeclarePriority(_declaration(TypeNameFormatter.DisplayName(_namelessType)));

    await Assert.That(_namelessType.AssemblyQualifiedName).IsNull()
      .Because("precondition: the tagged type really has no assembly-qualified name");
    await Assert.That(declared).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("a type with no assembly-qualified name is indexed under its display name, so it still resolves");
  }
}
