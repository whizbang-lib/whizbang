// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core;

/// <summary>
/// Marks a package that activates itself (#2502): every assembly compiled with the Whizbang generators against it calls
/// <paramref name="registrar"/>'s public static <c>Register()</c> from its generated module initializer, so the package
/// registers itself with <see cref="ServiceRegistrationCallbacks"/> before <c>AddWhizbang()</c> runs, even when no code
/// names any of its types. A reference and configuration are then all a host needs.
/// </summary>
/// <param name="registrar">The package's public static class with a public static, parameterless <c>Register()</c>.</param>
/// <docs>fundamentals/storage/azure-blob#turnkey-activation</docs>
/// <tests>tests/Whizbang.Generators.Tests/ServiceAssemblyFactsGeneratorTests.cs:AReferencedTurnkeyPackage_IsRegistered_FromTheModuleInitializerAsync</tests>
/// <tests>tests/Whizbang.Storage.AzureBlob.Tests/AzureBlobStorageTurnkeyTests.cs:ThePackage_DeclaresItselfTurnkey_NamingTheRegistrarTheGeneratedInitializerCallsAsync</tests>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class WhizbangTurnkeyPackageAttribute(Type registrar) : Attribute {
  /// <summary>The package's registrar.</summary>
  public Type Registrar { get; } = registrar;
}
