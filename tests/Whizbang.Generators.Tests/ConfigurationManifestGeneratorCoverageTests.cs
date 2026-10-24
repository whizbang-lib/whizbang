// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators;
using static Whizbang.Generators.Tests.ConfigurationManifestGeneratorTests;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The bindings the configuration manifest generator declines, the shapes of bound type it walks, and its syntax
/// filter. Each declined shape is one the manifest cannot state truthfully: a section whose path is not a
/// compile-time constant, a section reached from an unknown parent, or a call that binds nothing.
/// </summary>
[Category("Generators")]
public class ConfigurationManifestGeneratorCoverageTests {
  [Test]
  [RequiresAssemblyFiles]
  public async Task BindingsWithoutAKnownPathOrBoundType_ProduceNoManifestAsync() {
    const string source = """
      using Microsoft.Extensions.Configuration;
      namespace App;
      public sealed class Options {
        public bool On { get; set; }
      }
      public static class Custom {
        public static void Bind(IConfiguration configuration, Options options) { }
      }
      public static class Binding {
        public static void Apply(IConfiguration configuration, IConfigurationSection section, ConfigurationSection concrete, string name, Options options) {
          // The key is not a compile-time constant.
          configuration.GetSection(name).Bind(options);
          // A null key is not a path.
          configuration.GetSection(null).Bind(options);
          // The section's parent is a section from elsewhere, so the full path is unknown.
          section.GetSection("Child").Bind(options);
          concrete.GetSection("Child").Bind(options);
          configuration.GetSection(name).GetSection("Child").Bind(options);
          // The key overload binds a key below the section, not the section.
          configuration.GetSection("Whizbang:Keyed").Bind("Inner", options);
          // Not a configuration binder.
          Custom.Bind(configuration.GetSection("Whizbang:Custom"), options);
          // Binds to a runtime type: nothing to walk.
          ConfigurationBinder.Get(configuration.GetSection("Whizbang:Typed"), typeof(Options));
          // The section is the instance being bound; the configuration it binds from is not a GetSection call.
          configuration.Bind(configuration.GetSection("Whizbang:AsInstance"));
          // An array is not a named options type.
          configuration.GetSection("Whizbang:Array").Get<string[]>();
        }
        public static void Generic<T>(IConfiguration configuration, T options) where T : class =>
          configuration.GetSection("Whizbang:Generic").Bind(options);
      }
      """;

    var (manifest, errors) = Run(source);

    await Assert.That(errors).IsEmpty();
    await Assert.That(manifest).IsEmpty()
      .Because("no call in the source binds a section whose path and bound type are both known");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task InvalidBindingCall_ProducesNoManifestAsync() {
    const string source = """
      using Microsoft.Extensions.Configuration;
      namespace App;
      public static class Binding {
        public static void Apply(IConfiguration configuration) => configuration.GetSection("Whizbang:Broken").Bind();
      }
      """;

    var (manifest, _) = Run(source);

    await Assert.That(manifest).IsEmpty().Because("a call that does not bind contributes nothing, and the generator does not fail on it");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ValueTypesAndUnbindableMembers_AreWalkedAsTheBinderReadsThemAsync() {
    const string source = """
      using System;
      using System.Globalization;
      using Microsoft.Extensions.Configuration;
      namespace App;
      public class BaseOptions {
        public string Inherited { get; set; } = "";
      }
      public sealed class Node {
        public string Name { get; set; } = "";
        public Node? Child { get; set; }
      }
      public sealed class Opaque { }
      public sealed class Options : BaseOptions {
        public static int Shared { get; set; }
        public int this[int index] { get => 0; set { } }
        public int ReadOnly { get; } = 1;
        public int PrivateSet { get; private set; }
        public int WriteOnly { set { } }
        internal int Hidden { get; set; }
        public TimeSpan Interval { get; set; }
        public DateTimeOffset At { get; set; }
        public Guid Id { get; set; }
        public Uri? Endpoint { get; set; }
        public CultureInfo? Culture { get; set; }
        public Node Root { get; set; } = new();
        public Opaque Marker { get; set; } = new();
        public dynamic? Anything { get; set; }
      }
      public static class Binding {
        public static void Apply(IConfiguration configuration, Options options) {
          configuration.GetSection("Whizbang:Walk").Bind(options);
          object boxed = options;
          configuration.GetSection("Whizbang:Boxed").Bind(boxed);
          configuration.GetSection("Whizbang:Count").Get<int>();
        }
      }
      """;

    var (manifest, errors) = Run(source);

    await Assert.That(errors).IsEmpty();
    await Assert.That(manifest).Contains(
      "\"Whizbang:Walk:Interval\", \"Whizbang:Walk:At\", \"Whizbang:Walk:Id\", \"Whizbang:Walk:Endpoint\", \"Whizbang:Walk:Culture\", "
      + "\"Whizbang:Walk:Root:Name\", \"Whizbang:Walk:Root:Child\", \"Whizbang:Walk:Marker\", \"Whizbang:Walk:Anything\", \"Whizbang:Walk:Inherited\"")
      .Because("value types are keys; a type that contains itself stops at the repeat; a type with nothing to bind is a key; "
        + "static, indexer, read-only, write-only and non-public members are not bound; inherited members are");
    await Assert.That(manifest).Contains(Binding("Whizbang:Boxed", "object") + " [\"Whizbang:Boxed\"]")
      .Because("an instance typed as object is bound as an opaque value");
    await Assert.That(manifest).Contains(Binding("Whizbang:Count", "int") + " [\"Whizbang:Count\"]");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task MalformedAndForeignAttributes_AreNotRequirementsAsync() {
    const string source = """
      using System.ComponentModel;
      using Microsoft.Extensions.Configuration;
      using Whizbang.Core.Capabilities;
      namespace App;
      public enum Mode {
        [Description("plain")]
        Plain,
        [RequiresPostgresExtension(null)]
        Broken,
      }
      public sealed class Options {
        [RequiresCapability(null, "x")]
        [System.Obsolete("old")]
        public bool Flag { get; set; }
        public Mode Mode { get; set; }
        [ConfigurationKeyName(null)]
        public int Unnamed { get; set; }
        [RequiresCapability("broker", "delayed_exchange", Feature = "Delayed delivery")]
        public Inner Delay { get; set; } = new();
      }
      public sealed class Inner {
        public int Seconds { get; set; }
      }
      public static class Binding {
        public static void Apply(IConfiguration configuration, Options options) =>
          configuration.GetSection("Whizbang:Attrs").Bind(options);
      }
      """;

    var (manifest, errors) = Run(source);

    await Assert.That(errors).IsEmpty();
    await Assert.That(manifest).Contains(Requirement("broker", "delayed_exchange", "Delayed delivery", "Whizbang:Attrs:Delay", null))
      .Because("any provider can be named, and a section-valued setting is required under the section's key");
    await Assert.That(manifest).Contains("\"Whizbang:Attrs:Flag\", \"Whizbang:Attrs:Mode\", \"Whizbang:Attrs:Unnamed\", \"Whizbang:Attrs:Delay:Seconds\"")
      .Because("a key name attribute without a name leaves the property's own name");
    await Assert.That(manifest).DoesNotContain("\"x\"").Because("a requirement without a provider names no capability");
    await Assert.That(manifest).DoesNotContain("Broken").Because("a requirement without an extension names no capability");
    await Assert.That(manifest).DoesNotContain("plain");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AttributesMissingTheirArguments_AreIgnoredWhileTheCodeIsEditedAsync() {
    const string source = """
      using Microsoft.Extensions.Configuration;
      using Whizbang.Core.Capabilities;
      namespace App;
      public sealed class Options {
        [ConfigurationKeyName]
        [RequiresPostgresExtension]
        [RequiresCapability("broker")]
        public bool Half { get; set; }
      }
      public static class Binding {
        public static void Apply(IConfiguration configuration, Options options) =>
          configuration.GetSection("Whizbang:Editing").Bind(options);
      }
      """;

    var (manifest, errors) = Run(source);

    await Assert.That(errors).IsNotEmpty().Because("each attribute is missing a required argument");
    await Assert.That(manifest).Contains(Binding("Whizbang:Editing", "global::App.Options") + " [\"Whizbang:Editing:Half\"], []")
      .Because("an attribute without its arguments renames nothing and requires nothing, and the generator still runs");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task RepeatedBinding_IsEmittedOnceAndBindingsAreOrderedAsync() {
    const string source = """
      using Microsoft.Extensions.Configuration;
      namespace App;
      public sealed class Options {
        public string Name { get; set; } = "a\"b";
      }
      public sealed class Other {
        public int Size { get; set; }
      }
      public static class Binding {
        public static void Apply(IConfiguration configuration, Options options, Other other) {
          configuration.GetSection("Whizbang:Z").Bind(options);
          configuration.GetSection("Whizbang:A").Bind(other);
          configuration.GetSection("Whizbang:A").Bind(options);
          configuration.GetSection("Whizbang:Z").Bind(options);
          configuration.GetSection("Quoted\"Key").Bind(other);
        }
      }
      """;

    var (manifest, errors) = Run(source);

    await Assert.That(errors).IsEmpty();
    var a = manifest.IndexOf(Binding("Whizbang:A", "global::App.Options"), StringComparison.Ordinal);
    var aOther = manifest.IndexOf(Binding("Whizbang:A", "global::App.Other"), StringComparison.Ordinal);
    var z = manifest.IndexOf(Binding("Whizbang:Z", "global::App.Options"), StringComparison.Ordinal);
    await Assert.That(a).IsGreaterThanOrEqualTo(0);
    await Assert.That(aOther).IsGreaterThan(a).Because("bindings are ordered by section, then by options type");
    await Assert.That(z).IsGreaterThan(aOther);
    await Assert.That(manifest.LastIndexOf(Binding("Whizbang:Z", "global::App.Options"), StringComparison.Ordinal)).IsEqualTo(z)
      .Because("the same section bound to the same type twice is one binding");
    await Assert.That(manifest).Contains("\"Quoted\\\"Key\"").Because("a key is emitted as an escaped string literal");
  }

  [Test]
  [Arguments("configuration.GetSection(\"A\").Bind(options)", true)]
  [Arguments("configuration.GetSection(\"A\").Get<Options>()", true)]
  [Arguments("ConfigurationBinder.Bind(configuration.GetSection(\"A\"), options)", true)]
  [Arguments("services.Configure<Options>(configuration.GetSection(\"A\"))", true)]
  [Arguments("configuration.Bind(options)", false)]
  [Arguments("configuration.GetSection(\"A\").Use(options)", false)]
  [Arguments("Bind(configuration.GetSection(\"A\"), options)", false)]
  [Arguments("configuration.GetSection(\"A\", \"B\").Bind(options)", false)]
  [Arguments("Helper.Bind(Load(\"A\"), options)", false)]
  public async Task IsSectionBindingCandidate_AdmitsOnlyABinderCallOnAGetSectionAsync(string expression, bool expected) {
    var node = SyntaxFactory.ParseExpression(expression);

    await Assert.That(ConfigurationManifestGenerator.IsSectionBindingCandidate(node)).IsEqualTo(expected);
  }

  [Test]
  public async Task IsSectionBindingCandidate_RejectsANodeThatIsNotAnInvocationAsync() {
    var node = SyntaxFactory.ParseExpression("configuration.Value");

    await Assert.That(node).IsTypeOf<MemberAccessExpressionSyntax>();
    await Assert.That(ConfigurationManifestGenerator.IsSectionBindingCandidate(node)).IsFalse();
  }
}
