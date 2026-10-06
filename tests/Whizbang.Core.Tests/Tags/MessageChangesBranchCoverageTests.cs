// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Branch coverage for <see cref="MessageChanges"/>' property discovery: a serializer property that
/// carries no member metadata (a hand-built contract, as a custom resolver produces) is reported by
/// its serializer name, while one that does carry its member reports the member's name.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tags/MessageChanges.cs</code-under-test>
[Category("Core")]
[Category("Tags")]
public class MessageChangesBranchCoverageTests {

  public sealed record HandBuiltContractEvent {
    public string ClrName { get; init; } = "";
    public string Unmapped { get; init; } = "";
  }

  /// <summary>
  /// Describes <see cref="HandBuiltContractEvent"/> by hand, the way a custom contract resolver
  /// does: one property with its member attached as the attribute provider, one without.
  /// </summary>
  private sealed class HandBuiltContractResolver : IJsonTypeInfoResolver {
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) {
      if (type != typeof(HandBuiltContractEvent)) {
        return null;
      }
      var stringInfo = JsonMetadataServices.CreateValueInfo<string>(options, JsonMetadataServices.StringConverter);
      return JsonMetadataServices.CreateObjectInfo<HandBuiltContractEvent>(options, new JsonObjectInfoValues<HandBuiltContractEvent> {
        ObjectCreator = () => new HandBuiltContractEvent(),
        PropertyMetadataInitializer = _ => {
          var withMember = JsonMetadataServices.CreatePropertyInfo<string>(options, new JsonPropertyInfoValues<string> {
            IsProperty = true,
            IsPublic = true,
            DeclaringType = typeof(HandBuiltContractEvent),
            PropertyName = nameof(HandBuiltContractEvent.ClrName),
            JsonPropertyName = "clr_wire",
            Getter = o => ((HandBuiltContractEvent)o).ClrName,
            PropertyTypeInfo = stringInfo,
          });
          withMember.AttributeProvider = typeof(HandBuiltContractEvent).GetProperty(nameof(HandBuiltContractEvent.ClrName));
          var withoutMember = JsonMetadataServices.CreatePropertyInfo<string>(options, new JsonPropertyInfoValues<string> {
            IsProperty = true,
            IsPublic = true,
            DeclaringType = typeof(HandBuiltContractEvent),
            PropertyName = nameof(HandBuiltContractEvent.Unmapped),
            JsonPropertyName = "unmapped_wire",
            Getter = o => ((HandBuiltContractEvent)o).Unmapped,
            PropertyTypeInfo = stringInfo,
          });
          // No member metadata at all: the only name this property has is its serializer name.
          withoutMember.AttributeProvider = null;
          return [withMember, withoutMember];
        },
        SerializeHandler = null,
      });
    }
  }

  /// <summary>
  /// Changed-property names are matched against model members, so a property with a member must
  /// report the member's name (not its wire name), and one without any member must still be
  /// reported, by the only name it has, rather than dropped or crash discovery.
  /// </summary>
  [Test]
  public async Task ForEvent_PropertyWithoutMemberMetadata_IsReportedByItsSerializerNameAsync() {
    // Highest priority so this resolver, which answers only for its own probe type, is the one
    // consulted for it regardless of what else is registered.
    JsonContextRegistry.RegisterContext(new HandBuiltContractResolver(), priority: 10_000);

    var changes = MessageChanges.ForEvent(typeof(HandBuiltContractEvent));

    await Assert.That(changes.EventProperties).IsEquivalentTo(["ClrName", "unmapped_wire"])
      .Because("a property carrying its member reports the member's name; one with no member reports its serializer name");
  }
}
