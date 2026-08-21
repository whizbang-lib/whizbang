using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for the routing-binding half of <see cref="TagPolicyValidator"/> (topology arc
/// phase 8): the <c>sys-</c> reserved prefix extends to <see cref="TagOptions.RouteNamespace"/>
/// bindings (a consumer cannot bind a sys-* tag the framework doesn't ship), and a message
/// type's route-bound tags may map to at most ONE TransportNamespace key (one type cannot ride
/// two broker namespaces).
/// </summary>
[Category("Core")]
[Category("Tags")]
public class TagPolicyValidatorRouteNamespaceTests {
  #region Reserved sys- prefix on routing bindings

  [Test]
  public async Task Validate_RouteBindingOnUnknownSysTag_ThrowsAsync() {
    // Binding sys-mine routes a tag the framework never shipped — that MINTS a reservation
    // under the sys- namespace exactly like a tag attribute would, so it fails the same way.
    var bindings = new Dictionary<string, string> { ["sys-mine"] = "bulk" };

    var ex = await Assert.That(() => TagPolicyValidator.Validate([], _noCoalesce(), bindings))
      .Throws<TagPolicyConfigurationException>();

    await Assert.That(ex!.Message).Contains("sys-mine");
    await Assert.That(ex.Message).Contains("reserved");
  }

  [Test]
  public async Task Validate_RouteBindingOnFrameworkSysTag_DoesNotThrowAsync() {
    // Routing an EXISTING framework tag (e.g. sys-audit to a bulk namespace) is deliberate
    // opt-in to a traffic class for framework traffic — it mints nothing new.
    var bindings = new Dictionary<string, string> { [SystemTags.AUDIT] = "bulk" };

    await Assert.That(() => TagPolicyValidator.Validate([], _noCoalesce(), bindings))
      .ThrowsNothing();
  }

  [Test]
  public async Task Validate_RouteBindingOnPlainApplicationTag_DoesNotThrowAsync() {
    var bindings = new Dictionary<string, string> { ["bulk-import"] = "bulk" };

    await Assert.That(() => TagPolicyValidator.Validate([], _noCoalesce(), bindings))
      .ThrowsNothing();
  }

  [Test]
  public async Task Validate_NullRouteBindings_KeepsTodaysBehaviorAsync() {
    // The two-argument surface (pre-phase-8 callers) must validate exactly as before.
    await Assert.That(() => TagPolicyValidator.Validate([], _noCoalesce()))
      .ThrowsNothing();
  }

  #endregion

  #region Route-binding ambiguity (one type, one TransportNamespace)

  [Test]
  public async Task Validate_TypeRoutedToTwoDifferentKeys_ThrowsNamingTypeAndBothKeysAsync() {
    var registrations = new[] {
      _registration(typeof(TestRoutedEvent), "bulk-import"),
      _registration(typeof(TestRoutedEvent), "archive-feed")
    };
    var bindings = new Dictionary<string, string> {
      ["bulk-import"] = "bulk",
      ["archive-feed"] = "archive"
    };

    var ex = await Assert.That(() => TagPolicyValidator.Validate(registrations, _noCoalesce(), bindings))
      .Throws<TagPolicyConfigurationException>();

    await Assert.That(ex!.Message).Contains(typeof(TestRoutedEvent).FullName!);
    await Assert.That(ex.Message).Contains("bulk");
    await Assert.That(ex.Message).Contains("archive");
  }

  [Test]
  public async Task Validate_TypeWithTwoTagsRoutedToTheSameKey_DoesNotThrowAsync() {
    // Two tags mapping to ONE key is deterministic — the destination namespace is unambiguous.
    var registrations = new[] {
      _registration(typeof(TestRoutedEvent), "bulk-import"),
      _registration(typeof(TestRoutedEvent), "archive-feed")
    };
    var bindings = new Dictionary<string, string> {
      ["bulk-import"] = "bulk",
      ["archive-feed"] = "bulk"
    };

    await Assert.That(() => TagPolicyValidator.Validate(registrations, _noCoalesce(), bindings))
      .ThrowsNothing();
  }

  [Test]
  public async Task Validate_TwoTypesEachRoutedToOneKey_DoesNotThrowAsync() {
    var registrations = new[] {
      _registration(typeof(TestRoutedEvent), "bulk-import"),
      _registration(typeof(TestOtherRoutedEvent), "archive-feed")
    };
    var bindings = new Dictionary<string, string> {
      ["bulk-import"] = "bulk",
      ["archive-feed"] = "archive"
    };

    await Assert.That(() => TagPolicyValidator.Validate(registrations, _noCoalesce(), bindings))
      .ThrowsNothing();
  }

  #endregion

  #region Hosted-service startup seam

  [Test]
  public async Task StartAsync_RouteBindingOnUnknownSysTag_FailsHostStartAsync() {
    var options = new TagOptions();
    options.RouteNamespace("sys-mine", "bulk");
    var validator = new TagPolicyStartupValidator(options, () => []);

    await Assert.That(async () => await validator.StartAsync(CancellationToken.None))
      .Throws<TagPolicyConfigurationException>();
  }

  [Test]
  public async Task StartAsync_CleanRouteBindings_CompletesAsync() {
    var options = new TagOptions();
    options.RouteNamespace("bulk-import", "bulk");
    var validator = new TagPolicyStartupValidator(options, () => []);

    await Assert.That(async () => await validator.StartAsync(CancellationToken.None))
      .ThrowsNothing();
  }

  #endregion

  #region Helpers

  private static Dictionary<string, CoalescePolicyOptions> _noCoalesce() => [];

  private static MessageTagRegistration _registration(Type messageType, string tag) => new() {
    MessageType = messageType,
    AttributeType = typeof(SignalTagAttribute),
    Tag = tag,
    PayloadBuilder = _ => JsonSerializer.SerializeToElement(new { }),
    AttributeFactory = () => new SignalTagAttribute { Tag = tag }
  };

  internal sealed record TestRoutedEvent;

  internal sealed record TestOtherRoutedEvent;

  #endregion
}
