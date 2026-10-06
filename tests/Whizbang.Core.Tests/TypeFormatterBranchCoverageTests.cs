// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// Branch coverage for <see cref="TypeFormatter.FormatType"/>'s public-key-token rendering when the
/// owning assembly carries no token. Every assembly built in this repository is strong-named, so the
/// existing cases only ever render a hex token. An unsigned assembly reports either no token at all
/// (<see langword="null"/>) or an empty one; both must render as the conventional
/// <c>PublicKeyToken=null</c>, never as an empty value that no type-name parser accepts.
/// </summary>
/// <code-under-test>src/Whizbang.Core/TypeFormatter.cs</code-under-test>
public class TypeFormatterBranchCoverageTests {
  private const TypeQualifications ASSEMBLY_AND_TOKEN = TypeQualifications.Assembly | TypeQualifications.PublicKeyToken;

  /// <summary>An assembly whose identity is exactly the <see cref="AssemblyName"/> given.</summary>
  private sealed class NamedAssembly(AssemblyName name) : Assembly {
    public override AssemblyName GetName() => name;
    public override AssemblyName GetName(bool copiedName) => name;
  }

  /// <summary>A type that reports <paramref name="assembly"/> as its owning assembly.</summary>
  private sealed class TypeInAssembly(Assembly assembly) : TypeDelegator(typeof(TypeFormatterBranchCoverageTests)) {
    public override Assembly Assembly => assembly;
  }

  [Test]
  public async Task FormatType_AssemblyWithNoPublicKeyToken_RendersNullAsync() {
    var name = new AssemblyName("Unsigned.Probe");

    await Assert.That(name.GetPublicKeyToken()).IsNull()
      .Because("the setup must present an assembly with no token at all");

    var result = TypeFormatter.FormatType(new TypeInAssembly(new NamedAssembly(name)), ASSEMBLY_AND_TOKEN);

    await Assert.That(result).IsEqualTo("Unsigned.Probe, PublicKeyToken=null");
  }

  [Test]
  public async Task FormatType_AssemblyWithEmptyPublicKeyToken_RendersNullAsync() {
    var name = new AssemblyName("Unsigned.Probe");
    name.SetPublicKeyToken([]);

    await Assert.That(name.GetPublicKeyToken()).IsNotNull()
      .Because("the setup must present an empty, non-null token");

    var result = TypeFormatter.FormatType(new TypeInAssembly(new NamedAssembly(name)), ASSEMBLY_AND_TOKEN);

    await Assert.That(result).IsEqualTo("Unsigned.Probe, PublicKeyToken=null")
      .Because("an empty token means unsigned, and must render the same as an absent one");
  }
}
