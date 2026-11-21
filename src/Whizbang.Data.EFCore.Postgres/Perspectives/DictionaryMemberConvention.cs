// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>
/// Maps every registered dictionary member inside a perspective document as a value with its stored form, whatever
/// options the context was built with (#1911).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DictionaryMemberTypeMappingSourcePlugin"/> answers Entity Framework's type mapping question for a
/// dictionary, and rides the Whizbang options extension. A context built from a plain connection string, as a
/// consumer-registered or hand-built lens context often is, does not carry it, and Entity Framework then takes the
/// dictionary for a navigation and refuses the model. So the generated configuration also calls
/// <see cref="Apply"/>: it walks the complex types the model maps into a document and adds each dictionary member the
/// generator registered for that type (<see cref="DictionaryMemberForms.RegisterMember"/>) as a property with the same
/// <see cref="DictionaryMemberTypeMapping"/>. With the plugin present the member is already a property and is left
/// alone; the two agree because they hand Entity Framework the same mapping.
/// </para>
/// <para>
/// A member the configuration ignored (a promoted jsonb field is) stays ignored.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/dictionary-members</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DictionaryMemberConventionTests.cs</tests>
public static class DictionaryMemberConvention {
  /// <summary>Adds every registered dictionary member the model maps into a document and does not yet hold.</summary>
  /// <param name="modelBuilder">The builder, after the perspectives are configured.</param>
  public static void Apply(ModelBuilder modelBuilder) {
    ArgumentNullException.ThrowIfNull(modelBuilder);

    foreach (var entityType in modelBuilder.Model.GetEntityTypes()) {
      foreach (var complexType in entityType.GetComplexProperties().Select(complex => complex.ComplexType)) {
        _apply(complexType, inDocument: complexType.IsMappedToJson());
      }
    }
  }

  private static void _apply(IMutableComplexType complexType, bool inDocument) {
    if (inDocument) {
      foreach (var (name, memberType) in DictionaryMemberForms.MembersOf(complexType.ClrType)) {
        if (complexType.FindProperty(name) is null && !complexType.IsIgnored(name)
            && DictionaryMemberTypeMapping.For(memberType) is { } mapping) {
          complexType.AddProperty(name).SetTypeMapping(mapping);
        }
      }
    }

    foreach (var nested in complexType.GetComplexProperties().Select(nested => nested.ComplexType)) {
      _apply(nested, inDocument || nested.IsMappedToJson());
    }
  }
}
