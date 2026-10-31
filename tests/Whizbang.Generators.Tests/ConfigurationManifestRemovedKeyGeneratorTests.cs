// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using static Whizbang.Generators.Tests.ConfigurationManifestGeneratorTests;

namespace Whizbang.Generators.Tests;

/// <summary>
/// #1755: a removed configuration key is declared with <c>[RemovedConfigurationKey]</c>, on the assembly or on a
/// type (by convention the options type of its replacement), and the configuration manifest generator records it in
/// the assembly's manifest. The startup check reads the manifest, so a removed key needs no hand-kept table and a
/// third-party package declares its own the same way.
/// </summary>
[Category("Generators")]
public class ConfigurationManifestRemovedKeyGeneratorTests {
  [Test]
  [RequiresAssemblyFiles]
  public async Task AssemblyAttribute_IsRecorded_EvenWithNoBindingAsync() {
    const string source = """
      using Whizbang.Core.Configuration;
      [assembly: RemovedConfigurationKey("Whizbang:Old:Interval", "Whizbang:New:Interval")]
      namespace App;
      public static class Nothing { }
      """;

    var (manifest, errors) = Run(source);

    await Assert.That(errors).IsEmpty().Because("the generated manifest must compile in the declaring assembly");
    await Assert.That(manifest).Contains("\"TestAssembly\"")
      .Because("an assembly that binds nothing but declares a removed key still registers a manifest");
    await Assert.That(manifest).Contains(RemovedKey("Whizbang:Old:Interval", "Whizbang:New:Interval"));
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task TypeAttributes_EveryReplacementInOrder_OrNoneAsync() {
    const string source = """
      using Microsoft.Extensions.Configuration;
      using Whizbang.Core.Configuration;
      namespace App;
      [RemovedConfigurationKey("Whizbang:Old:Cadence", "Whizbang:Maintenance:Interval", "Whizbang:Maintenance:Cooldown")]
      [RemovedConfigurationKey("Whizbang:Old:Dropped")]
      public sealed class MaintenanceOptions {
        public int Interval { get; set; }
        public int Cooldown { get; set; }
      }
      [RemovedConfigurationKey("Whizbang:Old:Window", "Whizbang:Batch:Window")]
      public sealed record BatchOptions {
        public int Window { get; set; }
      }
      public static class Binding {
        public static void Apply(IConfiguration configuration, MaintenanceOptions options) =>
          configuration.GetSection("Whizbang:Maintenance").Bind(options);
      }
      """;

    var (manifest, errors) = Run(source);

    await Assert.That(errors).IsEmpty();
    await Assert.That(manifest).Contains(Binding("Whizbang:Maintenance", "global::App.MaintenanceOptions"))
      .Because("removed keys are recorded beside the bindings, in the same manifest");
    await Assert.That(manifest).Contains(RemovedKey("Whizbang:Old:Cadence", "Whizbang:Maintenance:Interval", "Whizbang:Maintenance:Cooldown"));
    await Assert.That(manifest).Contains(RemovedKey("Whizbang:Old:Dropped"))
      .Because("a key removed with nothing in its place is recorded with no replacement");
    await Assert.That(manifest).Contains(RemovedKey("Whizbang:Old:Window", "Whizbang:Batch:Window"))
      .Because("a record is a class, and options types are often records");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task RemovedKeys_AreOrderedByKey_AndADuplicateIsRecordedOnceAsync() {
    const string source = """
      using Whizbang.Core.Configuration;
      [assembly: RemovedConfigurationKey("Whizbang:Old:B", "Whizbang:New:B")]
      namespace App;
      [RemovedConfigurationKey("Whizbang:Old:A", "Whizbang:New:A")]
      [RemovedConfigurationKey("Whizbang:Old:B", "Whizbang:New:B")]
      public sealed class Holder { }
      """;

    var (manifest, errors) = Run(source);

    await Assert.That(errors).IsEmpty();
    var a = manifest.IndexOf(RemovedKey("Whizbang:Old:A", "Whizbang:New:A"), StringComparison.Ordinal);
    var b = manifest.IndexOf(RemovedKey("Whizbang:Old:B", "Whizbang:New:B"), StringComparison.Ordinal);
    await Assert.That(a).IsGreaterThanOrEqualTo(0);
    await Assert.That(b).IsGreaterThan(a).Because("the order is the same on every build, whatever order the declarations are found in");
    await Assert.That(manifest.LastIndexOf("\"Whizbang:Old:B\"", StringComparison.Ordinal)).IsEqualTo(manifest.IndexOf("\"Whizbang:Old:B\"", StringComparison.Ordinal))
      .Because("the same declaration in two places is one removed key");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task MalformedDeclarations_AreSkippedAsync() {
    const string source = """
      using Whizbang.Core.Configuration;
      namespace App;
      [RemovedConfigurationKey(null!, "Whizbang:New:A")]
      [RemovedConfigurationKey("Whizbang:Old:NullList", null!)]
      [RemovedConfigurationKey("Whizbang:Old:NullEntry", "Whizbang:New:Kept", null!)]
      public sealed class Holder { }
      """;

    var (manifest, errors) = Run(source);

    await Assert.That(errors).IsEmpty();
    await Assert.That(manifest).DoesNotContain("\"Whizbang:New:A\"")
      .Because("a declaration with no key names nothing to warn about");
    await Assert.That(manifest).Contains(RemovedKey("Whizbang:Old:NullList"))
      .Because("a null replacement list is no replacement");
    await Assert.That(manifest).Contains(RemovedKey("Whizbang:Old:NullEntry", "Whizbang:New:Kept"))
      .Because("a null replacement names nothing, so only the named ones are kept");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AttributeMissingItsKey_IsSkippedAsync() {
    const string source = """
      using Whizbang.Core.Configuration;
      namespace App;
      [RemovedConfigurationKey]
      public sealed class Holder { }
      """;

    var (manifest, errors) = Run(source);

    await Assert.That(errors).IsNotEmpty().Because("the declaration itself does not compile");
    await Assert.That(manifest).IsEmpty().Because("a declaration with no arguments records nothing, so there is nothing to register");
  }

  /// <summary>The generated text of one removed key.</summary>
  internal static string RemovedKey(string key, params string[] replacedBy) =>
    $"new global::Whizbang.Core.Configuration.RemovedConfigurationKeyEntry(\"{key}\", ["
    + string.Join(", ", replacedBy.Select(r => $"\"{r}\""))
    + "])";
}
