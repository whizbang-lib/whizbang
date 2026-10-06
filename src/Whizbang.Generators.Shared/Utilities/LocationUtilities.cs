// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Shared.Utilities;

/// <summary>
/// Where a diagnostic about a symbol is reported.
/// </summary>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/LocationUtilitiesTests.cs</tests>
public static class LocationUtilities {
  /// <summary>
  /// The symbol's first location, or <see cref="Location.None"/> when it has none (or there is no symbol).
  /// </summary>
  /// <remarks>
  /// A symbol declared in source always has a location, so an analyzer reporting on the declaration it is
  /// analyzing never gets <see cref="Location.None"/> here. Constructed symbols such as array types have
  /// none, and the fallback keeps a diagnostic reportable rather than throwing. Owning that fallback once
  /// keeps a branch no source declaration takes out of every analyzer.
  /// </remarks>
  public static Location FirstOrNone(ISymbol? symbol) => symbol?.Locations.FirstOrDefault() ?? Location.None;

  /// <summary>
  /// Where the attribute is written in source, or <paramref name="fallback"/> for an attribute that was
  /// not written in source (one read from a referenced assembly's metadata).
  /// </summary>
  /// <remarks>
  /// An analyzer reporting on an attribute of the declaration it is analyzing always gets the application
  /// site; the fallback serves an attribute that arrived through metadata, which has none.
  /// </remarks>
  public static Location ApplicationOrFallback(AttributeData attribute, Location fallback, CancellationToken cancellationToken) =>
    attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? fallback;
}
