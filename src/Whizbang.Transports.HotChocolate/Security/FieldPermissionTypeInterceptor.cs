// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using HotChocolate.Configuration;
using HotChocolate.Data.Filters;
using HotChocolate.Data.Sorting;
using HotChocolate.Language;
using HotChocolate.Resolvers;
using HotChocolate.Types.Descriptors;
using HotChocolate.Types.Descriptors.Definitions;
using Whizbang.Core.Security.Attributes;
using Whizbang.Transports.HotChocolate.Middleware;

namespace Whizbang.Transports.HotChocolate;

/// <summary>
/// Enforces <see cref="FieldPermissionAttribute"/> across the schema. Every object type field backed by a
/// protected property returns the value <see cref="FieldPermissionMasking"/> produces to a caller whose scope
/// lacks the permission, and no protected property is offered in a filter or sort input type.
/// </summary>
/// <remarks>
/// <para>
/// <c>AddWhizbangLenses()</c> registers it, so it covers the generated lens row types' <c>data</c> fields and
/// every model type nested inside them. A schema built without <c>AddWhizbangLenses()</c> registers it with
/// <c>TryAddTypeInterceptor&lt;FieldPermissionTypeInterceptor&gt;()</c>.
/// </para>
/// <para>
/// A protected field is nullable in the schema, because a hidden value is returned as <c>null</c>. The
/// caller's scope is read from <see cref="Whizbang.Core.Security.IScopeContextAccessor.Current"/>; a request
/// with no scope, or a host with no accessor registered, sees every protected field masked.
/// </para>
/// <para>
/// Filtering and sorting are left out for protected fields because either would reveal the value a caller
/// cannot see: a filter compares it and an ordering ranks it.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/security#column-level-security</docs>
/// <tests>tests/Whizbang.Transports.HotChocolate.Tests/Unit/FieldPermissionTypeInterceptorTests.cs</tests>
public sealed class FieldPermissionTypeInterceptor : TypeInterceptor {
  /// <inheritdoc />
  public override void OnAfterInitialize(ITypeDiscoveryContext discoveryContext, DefinitionBase definition) {
    var typeInspector = discoveryContext.DescriptorContext.TypeInspector;
    switch (definition) {
      case ObjectTypeDefinition objectType:
        foreach (var field in objectType.Fields) {
          if (_protection(field.Member) is { } protection) {
            field.Type = _nullable(field.Type, typeInspector);
            field.FormatterDefinitions.Add(new ResultFormatterDefinition(_mask(protection)));
          }
        }
        break;
      case InterfaceTypeDefinition interfaceType:
        // An implementing object type's protected field becomes nullable, so the interface's must as well.
        foreach (var field in interfaceType.Fields.Where(f => _protection(f.Member) is not null)) {
          field.Type = _nullable(field.Type, typeInspector);
        }
        break;
      case FilterInputTypeDefinition filterType:
        foreach (var field in filterType.Fields.OfType<FilterFieldDefinition>().Where(f => _protection(f.Member) is not null)) {
          field.Ignore = true;
        }
        break;
      case SortInputTypeDefinition sortType:
        foreach (var field in sortType.Fields.OfType<SortFieldDefinition>().Where(f => _protection(f.Member) is not null)) {
          field.Ignore = true;
        }
        break;
    }
  }

  /// <summary>A protected property's permission and whether it is a string.</summary>
  private sealed record Protection(FieldPermissionAttribute Attribute, bool IsString);

  private static Protection? _protection(MemberInfo? member) =>
    member is PropertyInfo property
      && Attribute.GetCustomAttribute(property, typeof(FieldPermissionAttribute), inherit: true) is FieldPermissionAttribute attribute
      ? new Protection(attribute, property.PropertyType == typeof(string))
      : null;

  private static ResultFormatterDelegate _mask(Protection protection) =>
    (context, result) => FieldPermissionMasking.IsPermitted(RequestScope.Resolve(context.Services), protection.Attribute)
      ? result
      : FieldPermissionMasking.MaskValue(result, protection.Attribute.Masking, protection.IsString);

  /// <summary>
  /// The field's type with its outermost level made nullable. A field type inferred from the property, and one
  /// written as GraphQL syntax, are both covered; a schema type given directly is left as it is.
  /// </summary>
  private static TypeReference? _nullable(TypeReference? type, ITypeInspector typeInspector) => type switch {
    ExtendedTypeReference extended => extended.WithType(typeInspector.ChangeNullability(extended.Type, true)),
    SyntaxTypeReference { Type: NonNullTypeNode nonNull } syntax => syntax.WithType(nonNull.Type),
    _ => type,
  };
}
