// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using HotChocolate;
using HotChocolate.Language;
using HotChocolate.Types;
using Whizbang.Core;

namespace Whizbang.Transports.HotChocolate.Types;

/// <summary>
/// The GraphQL scalar of one <c>[WhizbangId]</c> type, named after it. On the wire it is a UUID string, the same
/// as the <c>UUID</c> scalar; on input it goes through the id's own <c>From(Guid)</c>, so a Guid the id refuses
/// (not UUIDv7, or empty for an id declared with <c>AcceptAnyVersion</c>) fails input coercion with a GraphQL
/// error instead of reaching the resolver.
/// </summary>
/// <docs>apis/graphql/setup</docs>
/// <tests>tests/Whizbang.Transports.HotChocolate.Tests/Unit/WhizbangIdTypeTests.cs</tests>
internal sealed class WhizbangIdType : ScalarType {
  /// <summary>The error code of a value that is not a valid id.</summary>
  internal const string INVALID_ID_CODE = "WHIZBANG_INVALID_ID";

  private readonly Func<Guid, object> _fromGuid;

  /// <summary>Creates the scalar of an id type.</summary>
  /// <param name="idType">The <c>[WhizbangId]</c> struct type; its name is the scalar's name.</param>
  /// <param name="fromGuid">The id's <c>From(Guid)</c>, from <see cref="WhizbangIdProviderRegistry.GetGuidConversions"/>.</param>
  public WhizbangIdType(Type idType, Func<Guid, object> fromGuid)
      : base(idType.Name, BindingBehavior.Implicit) {
    RuntimeType = idType;
    _fromGuid = fromGuid;
    Description = $"A {idType.Name}, written as a UUID string.";
#pragma warning disable S1075 // the URL of the UUID specification, published in the schema as @specifiedBy; not a host
    SpecifiedBy = new Uri("https://www.rfc-editor.org/rfc/rfc9562");
#pragma warning restore S1075
  }

  /// <inheritdoc />
  public override Type RuntimeType { get; }

  /// <inheritdoc />
  public override bool IsInstanceOfType(IValueNode valueSyntax) =>
    valueSyntax is StringValueNode or NullValueNode;

  /// <inheritdoc />
  public override object? ParseLiteral(IValueNode valueSyntax) => valueSyntax switch {
    NullValueNode => null,
    StringValueNode text => _parse(text.Value),
    _ => throw _invalid($"The {Name} scalar takes a UUID string.")
  };

  /// <inheritdoc />
  public override IValueNode ParseValue(object? runtimeValue) => runtimeValue switch {
    null => NullValueNode.Default,
    IWhizbangId id when id.GetType() == RuntimeType => new StringValueNode(id.ToGuid().ToString()),
    _ => throw _invalid($"The {Name} scalar cannot write this value.")
  };

  /// <inheritdoc />
  public override IValueNode ParseResult(object? resultValue) => resultValue switch {
    string text => new StringValueNode(text),
    _ => ParseValue(resultValue)
  };

  /// <inheritdoc />
  public override bool TrySerialize(object? runtimeValue, out object? resultValue) {
    switch (runtimeValue) {
      case null:
        resultValue = null;
        return true;
      case IWhizbangId id when id.GetType() == RuntimeType:
        resultValue = id.ToGuid().ToString();
        return true;
      default:
        resultValue = null;
        return false;
    }
  }

  /// <summary>
  /// Reads a value from a variable. A string that is not a valid id throws a <see cref="SerializationException"/>
  /// that says why, rather than returning false, which HotChocolate reports without the reason.
  /// </summary>
  public override bool TryDeserialize(object? resultValue, out object? runtimeValue) {
    switch (resultValue) {
      case null:
        runtimeValue = null;
        return true;
      case string text:
        runtimeValue = _parse(text);
        return true;
      case IWhizbangId id when id.GetType() == RuntimeType:
        runtimeValue = id;
        return true;
      default:
        runtimeValue = null;
        return false;
    }
  }

  private object _parse(string text) {
    if (!Guid.TryParse(text, out var guid)) {
      throw _invalid($"The value is not a valid {Name}: it is not a UUID.");
    }

    try {
      return _fromGuid(guid);
    } catch (ArgumentException ex) {
      throw _invalid($"The value is not a valid {Name}: {ex.Message}");
    }
  }

  private SerializationException _invalid(string message) =>
    new(ErrorBuilder.New().SetMessage(message).SetCode(INVALID_ID_CODE).Build(), this);
}
