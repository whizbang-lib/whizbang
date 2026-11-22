// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Whizbang.Core;


namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>
/// Entity Framework's mapping of a strongly-typed id: a <c>uuid</c> scalar, so inside a perspective document the id is
/// stored as a UUID string, the same as a <see cref="Guid"/> and as the transport writes it, not as the object
/// <c>{"Value": ...}</c> Entity Framework would otherwise discover (#2518).
/// </summary>
/// <remarks>
/// <para>
/// Answered by <see cref="WhizbangIdTypeMappingSourcePlugin"/> for every id type with a registered
/// <see cref="WhizbangIdForm"/>, which every generated id has, at whatever depth Entity Framework's walk reaches it: a
/// document member, a member of a nested object, a member of a collection element. As a scalar, an id compares as a
/// scalar in a query (<c>CAST(data -&gt;&gt; 'OrderId' AS uuid) = @p</c>) and <c>Contains</c> over ids translates to
/// <c>= ANY (@ids)</c>.
/// </para>
/// <para>
/// The converter loads through the id's unchecked constructor, so a stored value the id's version rule refuses today
/// still loads.
/// </para>
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/WhizbangIdTypeMappingTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/WhizbangIdDocumentStorageTests.cs</tests>
public sealed class WhizbangIdTypeMapping : RelationalTypeMapping {
  /// <summary>The store type of a strongly-typed id outside a document.</summary>
  public const string STORE_TYPE = "uuid";

  private WhizbangIdTypeMapping(RelationalTypeMappingParameters parameters) : base(parameters) {
  }

  /// <summary>The mapping of an id type, when its form is registered.</summary>
  /// <param name="clrType">A member's type, nullable already unwrapped by Entity Framework.</param>
  /// <returns>The mapping, or <see langword="null"/> when the type is not a registered id.</returns>
  /// <remarks>One mapping per type for the life of the process: a mapping is immutable.</remarks>
  public static WhizbangIdTypeMapping? For(Type clrType) {
    ArgumentNullException.ThrowIfNull(clrType);
    if (_built.TryGetValue(clrType, out var existing)) {
      return existing;
    }
    if (!WhizbangIdForms.TryGet(clrType, out var form)) {
      return null;
    }
    return _built.GetOrAdd(clrType, _ => form.Accept(Factory.Instance));
  }

  private static readonly ConcurrentDictionary<Type, WhizbangIdTypeMapping> _built = new();

  /// <inheritdoc/>
  protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
    new WhizbangIdTypeMapping(parameters);

  private sealed class Factory : IWhizbangIdFormVisitor<WhizbangIdTypeMapping> {
    public static Factory Instance { get; } = new();

    public WhizbangIdTypeMapping Visit<[DynamicallyAccessedMembers(WhizbangIdForm.ID_MEMBERS)] TId>(WhizbangIdForm<TId> form)
      where TId : struct, IWhizbangId =>
      new(new RelationalTypeMappingParameters(
        new CoreTypeMappingParameters(
          typeof(TId),
          new ValueConverter<TId, Guid>(id => WhizbangIdValues<TId>.Store(id), guid => WhizbangIdValues<TId>.Load(guid)),
          new ValueComparer<TId>(favorStructuralComparisons: false),
          jsonValueReaderWriter: WhizbangIdJsonReaderWriter<TId>.Instance),
        storeType: STORE_TYPE,
        dbType: System.Data.DbType.Guid));
  }
}

/// <summary>The conversions a strongly-typed id's mapping is built from, named so Entity Framework can compile them.</summary>
/// <typeparam name="TId">The id type.</typeparam>
/// <docs>fundamentals/identity/whizbang-ids</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/WhizbangIdTypeMappingTests.cs</tests>
[SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
  Justification = "Entity Framework's converters and reader/writers are built per closed id type")]
public static class WhizbangIdValues<TId> where TId : struct, IWhizbangId {
  private static WhizbangIdForm<TId> _form =>
    WhizbangIdForms.TryGet(typeof(TId), out var form)
      ? (WhizbangIdForm<TId>)form
      : throw new InvalidOperationException($"No stored form is registered for {TypeNameFormatter.SimpleName(typeof(TId))}; its generated module initializer has not run.");

  /// <summary>The id a stored Guid holds, through the id's unchecked constructor.</summary>
  /// <param name="stored">The stored Guid.</param>
  /// <returns>The id.</returns>
  public static TId Load(Guid stored) => _form.Load(stored);

  /// <summary>The Guid an id stores.</summary>
  /// <param name="id">The id.</param>
  /// <returns>Its Guid.</returns>
  public static Guid Store(TId id) => id.ToGuid();
}

/// <summary>
/// Entity Framework's JSON reader and writer for a strongly-typed id: writes and reads a UUID string (#2518).
/// </summary>
/// <typeparam name="TId">The id type.</typeparam>
/// <docs>fundamentals/identity/whizbang-ids</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/WhizbangIdTypeMappingTests.cs</tests>
[SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
  Justification = "Entity Framework's reader/writers are built per closed id type")]
public sealed class WhizbangIdJsonReaderWriter<TId> : JsonValueReaderWriter<TId> where TId : struct, IWhizbangId {
  /// <summary>The single instance Entity Framework uses.</summary>
  public static WhizbangIdJsonReaderWriter<TId> Instance { get; } = new();

  private WhizbangIdJsonReaderWriter() {
  }

  /// <inheritdoc/>
  public override TId FromJsonTyped(ref Utf8JsonReaderManager manager, object? existingObject = null) {
    if (manager.CurrentReader.TokenType != JsonTokenType.String) {
      throw new JsonException(
        $"A stored {TypeNameFormatter.SimpleName(typeof(TId))} must be a UUID string, but the document holds {manager.CurrentReader.TokenType}");
    }
    return WhizbangIdValues<TId>.Load(manager.CurrentReader.GetGuid());
  }

  /// <inheritdoc/>
  public override void ToJsonTyped(Utf8JsonWriter writer, TId value) {
    ArgumentNullException.ThrowIfNull(writer);
    writer.WriteStringValue(WhizbangIdValues<TId>.Store(value));
  }

  /// <inheritdoc/>
  public override Expression ConstructorExpression =>
    Expression.Property(null, typeof(WhizbangIdJsonReaderWriter<TId>).GetProperty(nameof(Instance))!);
}

/// <summary>
/// Answers Entity Framework's type mapping question for every registered strongly-typed id with
/// <see cref="WhizbangIdTypeMapping"/>, so the id is a scalar rather than a complex type (#2518).
/// </summary>
/// <remarks>
/// A type mapping rather than a convention: Entity Framework asks the type mapping source while it discovers a complex
/// type's members, which is where it decides an id is a complex type of its own. A convention runs after that. Rides
/// the Whizbang options extension, which every generated perspective context carries.
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/WhizbangIdTypeMappingTests.cs</tests>
public sealed class WhizbangIdTypeMappingSourcePlugin : IRelationalTypeMappingSourcePlugin {
  /// <inheritdoc/>
  public RelationalTypeMapping? FindMapping(in RelationalTypeMappingInfo mappingInfo) =>
    mappingInfo.ClrType is { } clrType ? WhizbangIdTypeMapping.For(clrType) : null;
}
