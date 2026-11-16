// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using HotChocolate.Configuration;
using HotChocolate.Data.Filters;
using HotChocolate.Types.Descriptors;
using HotChocolate.Types.Descriptors.Definitions;

namespace Whizbang.Transports.HotChocolate.Types;

/// <summary>
/// Makes a filter on a <c>[WhizbangId]</c> field compare the id itself (<c>id: { eq: "..." }</c>) rather than the
/// fields of the id struct (<c>id: { value: { eq: "..." } }</c>): before a filter input type registers its
/// dependencies, each field whose property is an id, or an optional id, gets that id's
/// <see cref="WhizbangIdOperationFilterInputType"/>. One filter type per id, made the first time a filter uses it.
/// </summary>
/// <remarks>
/// The filter type is an instance per id rather than a generic type closed over the id, so no type is made at run
/// time. An id with no recorded conversion (one not generated from <c>[WhizbangId]</c>) keeps HotChocolate's own
/// filter.
/// </remarks>
/// <docs>apis/graphql/filtering</docs>
/// <tests>tests/Whizbang.Transports.HotChocolate.Ids.Tests/Unit/WhizbangIdFilteringTests.cs</tests>
internal sealed class WhizbangIdFilterTypeInterceptor : TypeInterceptor {
  private readonly Dictionary<Type, WhizbangIdOperationFilterInputType> _filterTypes = [];

  /// <inheritdoc />
  public override void OnBeforeRegisterDependencies(ITypeDiscoveryContext discoveryContext, DefinitionBase definition) {
    if (definition is not FilterInputTypeDefinition filterType) {
      return;
    }

    // A field read from a property; operation fields and the and/or fields have no member.
    foreach (var field in filterType.Fields.OfType<FilterFieldDefinition>()) {
      if (field.Member is PropertyInfo property && _idTypeOf(property.PropertyType) is { } idType) {
        field.Type = TypeReference.Create(_filterTypeOf(idType, discoveryContext));
      }
    }
  }

  private static Type? _idTypeOf(Type propertyType) {
    var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
    return WhizbangIdTypeDiscoveryHandler.ConversionOf(type) is null ? null : type;
  }

  private WhizbangIdOperationFilterInputType _filterTypeOf(Type idType, ITypeDiscoveryContext discoveryContext) {
    if (!_filterTypes.TryGetValue(idType, out var filterType)) {
      filterType = new WhizbangIdOperationFilterInputType(idType, discoveryContext.DescriptorContext.InputParser);
      _filterTypes[idType] = filterType;
    }

    return filterType;
  }
}
