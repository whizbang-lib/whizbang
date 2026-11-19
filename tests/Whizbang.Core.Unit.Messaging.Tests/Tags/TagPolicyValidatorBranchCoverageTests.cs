// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Branch coverage for <see cref="TagPolicyValidator"/>'s one-TransportNamespace-per-type rule: a
/// tag with no routing binding contributes no namespace, so it can neither create nor hide an
/// ambiguity.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tags/TagPolicyValidator.cs</code-under-test>
[Category("Core")]
[Category("Tags")]
public class TagPolicyValidatorBranchCoverageTests {

  internal sealed record MixedRoutingEvent;

  private static Dictionary<string, CoalescePolicyOptions> _noCoalesce() => [];

  private static MessageTagRegistration _registration(Type messageType, string tag) => new() {
    MessageType = messageType,
    AttributeType = typeof(SignalTagAttribute),
    Tag = tag,
    PayloadBuilder = _ => JsonSerializer.SerializeToElement(new { }),
    AttributeFactory = () => new SignalTagAttribute { Tag = tag }
  };

  /// <summary>
  /// One routed tag and one unrouted tag on the same type is one namespace, not two. Counting the
  /// unrouted tag as a namespace would fail every host whose routed types also carry an ordinary
  /// signal tag.
  /// </summary>
  [Test]
  public async Task Validate_TypeWithOneRoutedAndOneUnroutedTag_DoesNotThrowAsync() {
    var registrations = new[] {
      _registration(typeof(MixedRoutingEvent), "bulk-import"),
      _registration(typeof(MixedRoutingEvent), "ui-refresh"),
    };
    var bindings = new Dictionary<string, string> { ["bulk-import"] = "bulk" };

    await Assert.That(() => TagPolicyValidator.Validate(registrations, _noCoalesce(), bindings))
      .ThrowsNothing();
  }

  /// <summary>
  /// A genuine two-namespace conflict is still reported when the type also carries an unrouted
  /// tag, and the message lists only the two real namespaces.
  /// </summary>
  [Test]
  public async Task Validate_TwoRoutedKeysPlusAnUnroutedTag_ThrowsListingOnlyTheRealKeysAsync() {
    var registrations = new[] {
      _registration(typeof(MixedRoutingEvent), "bulk-import"),
      _registration(typeof(MixedRoutingEvent), "ui-refresh"),
      _registration(typeof(MixedRoutingEvent), "archive-feed"),
    };
    var bindings = new Dictionary<string, string> {
      ["bulk-import"] = "bulk",
      ["archive-feed"] = "archive",
    };

    var ex = await Assert.That(() => TagPolicyValidator.Validate(registrations, _noCoalesce(), bindings))
      .Throws<TagPolicyConfigurationException>();

    await Assert.That(ex!.Message).Contains("('archive', 'bulk')")
      .Because("exactly the two bound namespaces are listed; the unrouted tag adds no entry");
  }
}
