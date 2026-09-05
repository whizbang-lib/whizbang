using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests;

/// <summary>
/// Guards the strong-naming contract: Whizbang assemblies carry a strong name
/// (identity for enterprise consumers; adding one later is binary-breaking, so it
/// must exist before v1.0), and any author-unsigned dependency must be a deliberate,
/// allowlisted decision rather than an accident. CS8002 is suppressed globally, so
/// this allowlist is the only guard against new unsigned references creeping in.
/// </summary>
public class AssemblyStrongNamingTests {
  /// <summary>
  /// Dependencies knowingly consumed without an author strong name. Each entry is a
  /// deliberate decision (the assembly is inert to the modern .NET runtime, which
  /// never validates strong names at load). Additions require the same deliberation:
  /// prefer a signed variant of the package when one exists.
  /// </summary>
  private static readonly string[] _unsignedReferenceAllowlist = [
    "Medo.Uuid7",
  ];

  [Test]
  public async Task WhizbangCore_IsStrongNamedAsync() {
    // Arrange
    var assemblyName = typeof(MessageId).Assembly.GetName();

    // Act
    var publicKeyToken = assemblyName.GetPublicKeyToken();

    // Assert
    await Assert.That(publicKeyToken).IsNotNull();
    await Assert.That(publicKeyToken!.Length).IsGreaterThan(0);
  }

  [Test]
  public async Task TestAssembly_IsStrongNamedAsync() {
    // Friend assemblies of a strong-named assembly must themselves be signed, or
    // every InternalsVisibleTo grant silently stops working.
    var publicKeyToken = typeof(AssemblyStrongNamingTests).Assembly.GetName().GetPublicKeyToken();

    await Assert.That(publicKeyToken).IsNotNull();
    await Assert.That(publicKeyToken!.Length).IsGreaterThan(0);
  }

  [Test]
  public async Task WhizbangCoreReferences_AreStrongNamedOrAllowlistedAsync() {
    // Arrange
    var references = typeof(MessageId).Assembly.GetReferencedAssemblies();

    // Act
    var unsignedOffenders = references
      .Where(reference => (reference.GetPublicKeyToken()?.Length ?? 0) == 0)
      .Select(reference => reference.Name ?? "<unnamed>")
      .Where(name => !_unsignedReferenceAllowlist.Contains(name))
      .OrderBy(name => name)
      .ToList();

    // Assert
    await Assert.That(unsignedOffenders).IsEmpty();
  }
}
