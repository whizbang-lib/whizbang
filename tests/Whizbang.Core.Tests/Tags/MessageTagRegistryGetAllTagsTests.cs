using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for the tag-enumeration surface (<see cref="IMessageTagRegistry.GetAllTags"/> /
/// <see cref="MessageTagRegistry.GetAllTags"/>) that startup validation of tag policies
/// rides on. The per-type <c>GetTagsFor</c> lookup cannot answer "which tags exist at all",
/// which the reserved-prefix and coalesce-ambiguity checks need.
/// </summary>
[Category("Core")]
[Category("Tags")]
public class MessageTagRegistryGetAllTagsTests {
  [Test]
  public async Task GetAllTags_DefaultInterfaceMember_ReturnsEmptyAsync() {
    // Hand-written registries (test fakes, older generated code) that predate the member
    // must keep compiling and simply contribute nothing to enumeration.
    IMessageTagRegistry registry = new LegacyShapedRegistry();

    var all = registry.GetAllTags().ToList();

    await Assert.That(all).IsEmpty();
  }

  [Test]
  public async Task GetAllTags_StaticAggregate_IncludesGeneratedRegistrationsFromThisAssemblyAsync() {
    // GetAllTagsProbeEvent below is public and tagged, so this assembly's source-generated
    // registry must surface it through the enumeration path. This locks the generator's
    // GetAllTags emission AND the static aggregation in one observable behavior.
    var all = MessageTagRegistry.GetAllTags().ToList();

    var probe = all.Where(r => r.MessageType == typeof(GetAllTagsProbeEvent)).ToList();
    await Assert.That(probe.Count).IsEqualTo(1);
    await Assert.That(probe[0].Tag).IsEqualTo("get-all-tags-probe");
  }

  [Test]
  public async Task GetAllTags_StaticAggregate_IsConsistentWithGetTagsForAsync() {
    // Every enumerated registration must also be reachable through the per-type lookup —
    // the two surfaces are views over the same generated data.
    var enumerated = MessageTagRegistry.GetAllTags()
      .Where(r => r.MessageType == typeof(GetAllTagsProbeEvent))
      .Select(r => r.Tag)
      .ToList();
    var byType = MessageTagRegistry.GetTagsFor(typeof(GetAllTagsProbeEvent))
      .Select(r => r.Tag)
      .ToList();

    await Assert.That(enumerated).IsEquivalentTo(byType);
  }

  private sealed class LegacyShapedRegistry : IMessageTagRegistry {
    public IEnumerable<MessageTagRegistration> GetTagsFor(Type messageType) {
      if (messageType == typeof(GetAllTagsProbeEvent)) {
        yield return new MessageTagRegistration {
          MessageType = typeof(GetAllTagsProbeEvent),
          AttributeType = typeof(SignalTagAttribute),
          Tag = "legacy",
          PayloadBuilder = _ => JsonSerializer.SerializeToElement(new { }),
          AttributeFactory = () => new SignalTagAttribute { Tag = "legacy" }
        };
      }
    }
  }
}

/// <summary>
/// Public on purpose: the MessageTagDiscoveryGenerator only discovers public types, and this
/// probe is what proves the generated registry participates in tag enumeration. The tag value
/// is inert — no hook is registered for it and no coalesce binding references it.
/// </summary>
[SignalTag(Tag = "get-all-tags-probe")]
public sealed record GetAllTagsProbeEvent(Guid Id);
