// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// The stored name of a member inside a promoted jsonb column, read from the persistence metadata. Metadata
/// without attribute providers cannot say which member a property came from; such metadata writes a member
/// under its own name, so only a property of exactly that name is taken as the member's.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/QueryTranslation/PhysicalJsonbContainmentRewriter.cs</code-under-test>
[Category("Shard1")]
public class PhysicalJsonbStoredNameTests {
  /// <summary>A document type described only by provider-less metadata, registered for this class alone.</summary>
  public sealed class ProviderlessDocument {
    public string Code { get; set; } = string.Empty;
  }

  /// <summary>A document type described by ordinary metadata, whose properties name their members.</summary>
  public sealed class ProvidedDocument {
    public string Code { get; set; } = string.Empty;
  }

  /// <summary>Describes <see cref="ProvidedDocument"/> as the default resolver does, and nothing else.</summary>
  private sealed class ProvidedResolver : IJsonTypeInfoResolver {
    private readonly DefaultJsonTypeInfoResolver _inner = new();

    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
      type == typeof(ProvidedDocument) ? _inner.GetTypeInfo(type, options) : null;
  }

  /// <summary>Describes <see cref="ProviderlessDocument"/> with every attribute provider cleared, and nothing else.</summary>
  private sealed class ProviderlessResolver : IJsonTypeInfoResolver {
    private readonly DefaultJsonTypeInfoResolver _inner = new() {
      Modifiers = {
        static info => {
          foreach (var property in info.Properties) {
            property.AttributeProvider = null;
          }
        },
      },
    };

    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
      type == typeof(ProviderlessDocument) ? _inner.GetTypeInfo(type, options) : null;
  }

  private static readonly Lazy<bool> _registered = new(() => {
    JsonContextRegistry.RegisterContext(new ProviderlessResolver());
    JsonContextRegistry.RegisterContext(new ProvidedResolver());
    return true;
  });

  [Test]
  public async Task WithoutAttributeProviders_AMemberIsFoundOnlyByItsOwnNameAsync() {
    _ = _registered.Value;

    var found = PhysicalJsonbContainmentRewriter.StoredNameFor(typeof(ProviderlessDocument), nameof(ProviderlessDocument.Code));
    var missing = PhysicalJsonbContainmentRewriter.StoredNameFor(typeof(ProviderlessDocument), "NotAMember");

    await Assert.That(found).IsEqualTo("Code")
      .Because("metadata without providers writes a member under its own name, so that name is the member's");
    await Assert.That(missing).IsNull()
      .Because("no property of that name means the metadata does not know the member, and the rewrite stands down");
  }

  [Test]
  public async Task WithAttributeProviders_AnUnknownMemberIsNotGuessedByNameAsync() {
    _ = _registered.Value;

    var found = PhysicalJsonbContainmentRewriter.StoredNameFor(typeof(ProvidedDocument), nameof(ProvidedDocument.Code));
    var missing = PhysicalJsonbContainmentRewriter.StoredNameFor(typeof(ProvidedDocument), "NotAMember");

    await Assert.That(found).IsEqualTo("Code");
    await Assert.That(missing).IsNull()
      .Because("metadata that names its members and names none of these does not know the member");
  }
}
