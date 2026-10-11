// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// The version text the heartbeat and the dead-letter generation tag report: the informational
/// version, else the assembly version, else <c>unknown</c>.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/LibraryVersion.cs</code-under-test>
[Category("Observability")]
public class AssemblyVersionTextTests {
  [Test]
  public async Task Informational_WinsAsync() {
    var assembly = typeof(AssemblyVersionText).Assembly;
    var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    await Assert.That(AssemblyVersionText.Of(assembly)).IsEqualTo(informational);
  }

  [Test]
  public async Task NoInformational_AssemblyVersionAsync() {
    var assembly = new BareAssembly(new AssemblyName("A") { Version = new Version(1, 2, 3, 4) });

    await Assert.That(AssemblyVersionText.Of(assembly)).IsEqualTo("1.2.3.4");
  }

  [Test]
  public async Task NoVersion_UnknownAsync() {
    var assembly = new BareAssembly(new AssemblyName("A"));

    await Assert.That(AssemblyVersionText.Of(assembly)).IsEqualTo("unknown");
  }

  /// <summary>An assembly with the given name and no attributes at all.</summary>
  private sealed class BareAssembly(AssemblyName name) : Assembly {
    // Typed Attribute[] although the signature says object[]: the runtime casts what this returns to
    // Attribute[], as a real assembly's answer always is.
    private static readonly Attribute[] _none = [];

    public override AssemblyName GetName() => name;
    public override AssemblyName GetName(bool copiedName) => name;
    public override object[] GetCustomAttributes(Type attributeType, bool inherit) => _none;
    public override object[] GetCustomAttributes(bool inherit) => _none;
  }
}
