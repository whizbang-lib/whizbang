// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq.Expressions;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The key column a fan-out targets rows by: one property of <typeparamref name="TModel"/>, named with
/// <see cref="FanOut.By{TModel}"/>.
/// </summary>
/// <typeparam name="TModel">The perspective's model.</typeparam>
/// <docs>fundamentals/perspectives/multi-stream#fan-out-from-another-stream</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveFanOutContractTests.cs:FanOutBy_KeepsTheKeySelectorTheTargetsWereDeclaredWithAsync</tests>
public sealed class FanOutKey<TModel> where TModel : class {
  internal FanOutKey(Expression<Func<TModel, object?>> selector) => Selector = selector;

  /// <summary>The selector as declared: <c>row =&gt; row.Key</c>.</summary>
  public Expression<Func<TModel, object?>> Selector { get; }
}

/// <summary>Names the key column of a fan-out.</summary>
/// <docs>fundamentals/perspectives/multi-stream#fan-out-from-another-stream</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveFanOutContractTests.cs:FanOutBy_KeepsTheKeySelectorTheTargetsWereDeclaredWithAsync</tests>
public static class FanOut {
  /// <summary>
  /// Targets the rows of <typeparamref name="TModel"/> whose <paramref name="key"/> equals the event's stream id.
  /// </summary>
  /// <remarks>
  /// The selector names one property of the model and nothing else (<c>o =&gt; o.CustomerId</c>); the generator reads
  /// it at build time, and WHIZ124 reports any other form. The key is a <see cref="PhysicalFieldAttribute"/> with an
  /// <see cref="IndexedAttribute"/>, a <see cref="Guid"/> or a strongly typed id over one.
  /// </remarks>
  /// <typeparam name="TModel">The perspective's model.</typeparam>
  /// <param name="key">The key property: <c>row =&gt; row.Key</c>.</param>
  /// <returns>The key.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveFanOutContractTests.cs:FanOutBy_ANullSelector_ThrowsAsync</tests>
  public static FanOutKey<TModel> By<TModel>(Expression<Func<TModel, object?>> key) where TModel : class {
    ArgumentNullException.ThrowIfNull(key);
    return new FanOutKey<TModel>(key);
  }
}
