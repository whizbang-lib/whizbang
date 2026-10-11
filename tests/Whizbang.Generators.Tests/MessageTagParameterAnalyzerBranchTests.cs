// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests;

/// <summary>
/// WHIZ090 suggests a property that the mismatched parameter could initialize: one of the same type
/// that can be set. A same-typed property with no setter cannot receive the value, so it is passed
/// over for one that can.
/// </summary>
/// <tests>src/Whizbang.Generators/Analyzers/MessageTagParameterAnalyzer.cs</tests>
public class MessageTagParameterAnalyzerBranchTests {
  [Test]
  [RequiresAssemblyFiles]
  public async Task SameTypedGetOnlyProperty_IsNotSuggestedAsync() {
    const string source = """
      using System;
      using Whizbang.Core.Attributes;

      namespace TestApp;

      [AttributeUsage(AttributeTargets.Class)]
      public class RegionTagAttribute : MessageTagAttribute {
        public RegionTagAttribute(string zone) { }
        public string Computed => "x";
        public string Region { get; set; } = "";
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MessageTagParameterAnalyzer>(source);
    var mismatch = diagnostics.Single(d => d.Id == "WHIZ090").GetMessage(CultureInfo.InvariantCulture);

    await Assert.That(mismatch).Contains("Region");
    await Assert.That(mismatch).DoesNotContain("Computed");
  }

  /// <summary>
  /// The analyzer recognizes the tag base by name, so a base it finds may offer nothing to set. With no
  /// settable property anywhere in the hierarchy there is nothing to suggest, and WHIZ090 still reports the
  /// mismatched parameter, with "?" in both suggestion slots rather than an invented name.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task NoSettablePropertyAnywhere_ReportsTheMismatchWithNoSuggestionAsync() {
    const string source = """
      using System;

      namespace Whizbang.Core.Attributes {
        public abstract class MessageTagAttribute : Attribute { }
      }

      namespace TestApp {
        [AttributeUsage(AttributeTargets.Class)]
        public class BareTagAttribute : Whizbang.Core.Attributes.MessageTagAttribute {
          public BareTagAttribute(int zone) { }
          public int Computed => 1;
        }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MessageTagParameterAnalyzer>(source);
    var mismatch = diagnostics.Single(d => d.Id == "WHIZ090");
    var message = mismatch.GetMessage(CultureInfo.InvariantCulture);

    await Assert.That(message).Contains("zone");
    await Assert.That(message).DoesNotContain("computed")
      .Because("a get-only property cannot receive the value, so it is not offered as the fix");
    await Assert.That(message).Contains("Rename to '?' to match property '?'")
      .Because("both suggestion slots read \"?\" when there is nothing to suggest");
  }
}
