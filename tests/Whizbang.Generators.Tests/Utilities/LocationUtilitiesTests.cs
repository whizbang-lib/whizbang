// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

extern alias shared;

using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using LocationUtilities = shared::Whizbang.Generators.Shared.Utilities.LocationUtilities;

namespace Whizbang.Generators.Tests.Utilities;

/// <summary>
/// The diagnostic location every analyzer takes for a symbol: its declaration when it has one, and
/// <see cref="Location.None"/> for a constructed symbol (an array type has no location) or no symbol.
/// No analyzer reports on such a symbol, so the fallback is pinned here rather than through one.
/// </summary>
/// <code-under-test>src/Whizbang.Generators.Shared/Utilities/LocationUtilities.cs</code-under-test>
public class LocationUtilitiesTests {
  [Test]
  public async Task FirstOrNone_DeclaredSymbol_IsItsDeclarationAsync() {
    var compilation = GeneratorTestHelper.CreateCompilation("namespace Sample; public class Declared { }");
    var declared = compilation.GetTypeByMetadataName("Sample.Declared")!;

    var location = LocationUtilities.FirstOrNone(declared);

    await Assert.That(location.IsInSource).IsTrue();
    await Assert.That(location).IsEqualTo(declared.Locations[0]);
  }

  [Test]
  public async Task FirstOrNone_SymbolWithoutLocations_IsNoneAsync() {
    var compilation = GeneratorTestHelper.CreateCompilation("namespace Sample;");
    var array = compilation.CreateArrayTypeSymbol(compilation.GetSpecialType(SpecialType.System_Int32));

    await Assert.That(array.Locations).IsEmpty()
      .Because("the control: a constructed array type is declared nowhere");
    await Assert.That(LocationUtilities.FirstOrNone(array)).IsEqualTo(Location.None);
  }

  [Test]
  public async Task FirstOrNone_NoSymbol_IsNoneAsync() {
    await Assert.That(LocationUtilities.FirstOrNone(null)).IsEqualTo(Location.None);
  }

  [Test]
  public async Task ApplicationOrFallback_SourceAttribute_IsWhereItIsWrittenAsync() {
    var compilation = GeneratorTestHelper.CreateCompilation("namespace Sample; [System.Obsolete] public class Old { }");
    var attribute = compilation.GetTypeByMetadataName("Sample.Old")!.GetAttributes().Single();
    var fallback = compilation.GetTypeByMetadataName("Sample.Old")!.Locations[0];

    var location = LocationUtilities.ApplicationOrFallback(attribute, fallback, CancellationToken.None);

    await Assert.That(location.IsInSource).IsTrue();
    var text = await location.SourceTree!.GetTextAsync();
    await Assert.That(text.ToString(location.SourceSpan)).IsEqualTo("System.Obsolete");
  }

  [Test]
  public async Task ApplicationOrFallback_MetadataAttribute_IsTheFallbackAsync() {
    var compilation = GeneratorTestHelper.CreateCompilation("namespace Sample;");
    var metadataAttribute = compilation.GetTypeByMetadataName("System.ObsoleteAttribute")!.GetAttributes().First();
    var fallback = Location.Create("fallback.cs", default, default);

    await Assert.That(metadataAttribute.ApplicationSyntaxReference).IsNull()
      .Because("the control: an attribute read from metadata was never written in this compilation");
    await Assert.That(LocationUtilities.ApplicationOrFallback(metadataAttribute, fallback, CancellationToken.None)).IsEqualTo(fallback);
  }
}
