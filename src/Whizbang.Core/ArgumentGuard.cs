// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Whizbang.Core;

/// <summary>
/// The expression form of <see cref="ArgumentNullException.ThrowIfNull(object?, string?)"/>, for
/// the places a statement cannot go: primary-constructor field and property initializers, and
/// arguments passed straight on.
/// </summary>
/// <remarks>
/// It replaces <c>x ?? throw new ArgumentNullException(nameof(x))</c>. The exception is the same
/// (type, <see cref="ArgumentException.ParamName"/> and message), but the null test now lives in the
/// runtime's <c>ThrowIfNull</c> instead of a branch in every constructor, so a constructor's
/// coverage measures what it does rather than one untested throw per dependency.
/// </remarks>
/// <tests>tests/Whizbang.Core.Tests/ArgumentGuardTests.cs</tests>
internal static class ArgumentGuard {
  /// <summary>Returns <paramref name="value"/>, or throws when it is null.</summary>
  /// <typeparam name="T">The argument's type.</typeparam>
  /// <param name="value">The argument.</param>
  /// <param name="paramName">Filled in by the compiler with the argument's expression.</param>
  /// <returns><paramref name="value"/>, known to be non-null.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
  internal static T NotNull<T>([NotNull] T? value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
      where T : class {
    ArgumentNullException.ThrowIfNull(value, paramName);
    return value;
  }
}
