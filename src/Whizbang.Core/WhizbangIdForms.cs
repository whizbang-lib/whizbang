// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Core;

/// <summary>
/// One strongly-typed id's stored form: how a stored Guid becomes the id and back, with the id's type in hand for a
/// storage integration that builds typed converters without reflection (#2518).
/// </summary>
/// <remarks>
/// The load goes through the id's constructor, which is unchecked on every id: a stored row is whatever was persisted,
/// whatever the id's version rule says today.
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids</docs>
/// <tests>tests/Whizbang.Core.Tests/ValueObjects/WhizbangIdFormsTests.cs</tests>
public abstract class WhizbangIdForm {
  private protected WhizbangIdForm() {
  }

  /// <summary>
  /// The members a storage integration's typed converters and comparers reach on an id type, kept by the trimmer.
  /// </summary>
  public const DynamicallyAccessedMemberTypes ID_MEMBERS =
    DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties;

  /// <summary>The id's type.</summary>
  public abstract Type IdType { get; }

  /// <summary>Calls the visitor with the id's type as a type argument.</summary>
  /// <typeparam name="TResult">What the visitor returns.</typeparam>
  /// <param name="visitor">The visitor.</param>
  /// <returns>The visitor's result.</returns>
  public abstract TResult Accept<TResult>(IWhizbangIdFormVisitor<TResult> visitor);
}

/// <summary>Visits a <see cref="WhizbangIdForm"/> with the id's type as a type argument.</summary>
/// <typeparam name="TResult">What a visit returns.</typeparam>
/// <docs>fundamentals/identity/whizbang-ids</docs>
/// <tests>tests/Whizbang.Core.Tests/ValueObjects/WhizbangIdFormsTests.cs</tests>
public interface IWhizbangIdFormVisitor<out TResult> {
  /// <summary>Visits the form of one id type.</summary>
  /// <typeparam name="TId">The id type.</typeparam>
  /// <param name="form">The form.</param>
  /// <returns>The visit's result.</returns>
  TResult Visit<[DynamicallyAccessedMembers(WhizbangIdForm.ID_MEMBERS)] TId>(WhizbangIdForm<TId> form) where TId : struct, IWhizbangId;
}

/// <summary>The stored form of the id type <typeparamref name="TId"/>.</summary>
/// <typeparam name="TId">The id type.</typeparam>
/// <docs>fundamentals/identity/whizbang-ids</docs>
/// <tests>tests/Whizbang.Core.Tests/ValueObjects/WhizbangIdFormsTests.cs</tests>
public sealed class WhizbangIdForm<[DynamicallyAccessedMembers(ID_MEMBERS)] TId> : WhizbangIdForm where TId : struct, IWhizbangId {
  private readonly Func<Guid, TId> _load;

  internal WhizbangIdForm(Func<Guid, TId> load) => _load = load;

  /// <inheritdoc/>
  public override Type IdType => typeof(TId);

  /// <summary>The id a stored Guid holds, unchecked: the load path.</summary>
  /// <param name="stored">The stored Guid.</param>
  /// <returns>The id.</returns>
  public TId Load(Guid stored) => _load(stored);

  /// <summary>The Guid an id stores.</summary>
  /// <param name="id">The id.</param>
  /// <returns>Its Guid.</returns>
  public Guid Store(TId id) => id.ToGuid();

  /// <inheritdoc/>
  public override TResult Accept<TResult>(IWhizbangIdFormVisitor<TResult> visitor) {
    ArgumentNullException.ThrowIfNull(visitor);
    return visitor.Visit(this);
  }
}

/// <summary>
/// The stored forms of every strongly-typed id the process has loaded, registered by each id's generated module
/// initializer (#2518).
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids</docs>
/// <tests>tests/Whizbang.Core.Tests/ValueObjects/WhizbangIdFormsTests.cs</tests>
public static class WhizbangIdForms {
  private static readonly ConcurrentDictionary<Type, WhizbangIdForm> _byIdType = new();

  /// <summary>Registers an id type's stored form. The first registration of a type wins.</summary>
  /// <typeparam name="TId">The id type.</typeparam>
  /// <param name="load">The id's unchecked load from a stored Guid: its constructor.</param>
  public static void Register<[DynamicallyAccessedMembers(WhizbangIdForm.ID_MEMBERS)] TId>(Func<Guid, TId> load)
    where TId : struct, IWhizbangId {
    ArgumentNullException.ThrowIfNull(load);
    _byIdType.TryAdd(typeof(TId), new WhizbangIdForm<TId>(load));
  }

  /// <summary>The stored form of an id type, when one is registered.</summary>
  /// <param name="idType">The id type.</param>
  /// <param name="form">The form, when registered.</param>
  /// <returns>Whether a form is registered for the type.</returns>
  public static bool TryGet(Type idType, [NotNullWhen(true)] out WhizbangIdForm? form) {
    ArgumentNullException.ThrowIfNull(idType);
    return _byIdType.TryGetValue(idType, out form);
  }
}
