// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

extern alias shared;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Generators;
using PolymorphicModelDiscovery = shared::Whizbang.Generators.Shared.Models.PolymorphicModelDiscovery;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// The model walks behind WHIZ810, WHIZ811 and the polymorphic-storage decision never descend into a
/// non-collection <c>System</c> type: the walk stops at the type itself, so <c>string</c>, <c>Guid</c>,
/// <c>Uri</c> and their kin contribute nothing whether they are a property's type or a generic
/// argument, and a reportable member declared after them is still found.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/PerspectiveModelDictionaryAnalyzer.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/PerspectiveModelPolymorphicAnalyzer.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators.Shared/Models/PolymorphicModelDiscovery.cs</code-under-test>
public class SystemTypeWalkTests {
  // Every System type the walks once special-cased, as a direct property and as a generic argument
  // (a collection's element and a user generic's argument), ahead of the member each test expects.
  private const string SYSTEM_MEMBERS = """
      public string Name { get; set; } = "";
      public DateTime At { get; set; }
      public DateTimeOffset AtOffset { get; set; }
      public TimeSpan Span { get; set; }
      public Guid Key { get; set; }
      public decimal Amount { get; set; }
      public Uri Link { get; set; } = new("https://example.invalid");
      public Version Release { get; set; } = new(1, 0);
      public DateOnly Day { get; set; }
      public TimeOnly Time { get; set; }
      public List<Guid> Keys { get; set; } = [];
      public List<Uri> Links { get; set; } = [];
      public Holder<Version> Held { get; set; } = new();
      public Holder<TimeOnly> HeldTime { get; set; } = new();
    """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task DictionaryAnalyzer_SystemTypesContributeNothing_AndALaterDictionaryIsReportedAsync() {
    var source = $$"""
      using System;
      using System.Collections.Generic;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record WalkEvent : IEvent;

      public class Holder<T> {
        public T? Value { get; set; }
      }

      public class Inner {
        public Dictionary<string, int> Map { get; set; } = new();
      }

      public class WalkModel {
        {{SYSTEM_MEMBERS}}
        public Holder<Inner> Nested { get; set; } = new();
        public Dictionary<string, Guid> Lookup { get; set; } = new();
      }

      public class WalkPerspective : IPerspectiveFor<WalkModel, WalkEvent> {
        public WalkModel Apply(WalkModel currentData, WalkEvent @event) => currentData;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelDictionaryAnalyzer>(source);
    var reported = _reportedMembers(diagnostics, "WHIZ810", ["Map", "Lookup"]);

    await Assert.That(reported).IsEquivalentTo(["Map", "Lookup"]);
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ810").All(d => _mentionsAny(d, ["Map", "Lookup"]))).IsTrue()
      .Because("no System-typed member, direct or as a generic argument, is reported");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task PolymorphicAnalyzer_SystemTypesContributeNothing_AndALaterAbstractMemberIsReportedAsync() {
    var source = $$"""
      using System;
      using System.Collections.Generic;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record WalkEvent : IEvent;

      public class Holder<T> {
        public T? Value { get; set; }
      }

      public abstract class Shape {
        public string Label { get; set; } = "";
      }

      public class Inner {
        public Shape? Outline { get; set; }
      }

      public class WalkModel {
        {{SYSTEM_MEMBERS}}
        public Holder<Inner> Nested { get; set; } = new();
        public Shape? Primary { get; set; }
      }

      public class WalkPerspective : IPerspectiveFor<WalkModel, WalkEvent> {
        public WalkModel Apply(WalkModel currentData, WalkEvent @event) => currentData;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelPolymorphicAnalyzer>(source);
    var reported = _reportedMembers(diagnostics, "WHIZ811", ["Outline", "Primary"]);

    await Assert.That(reported).IsEquivalentTo(["Outline", "Primary"]);
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ811").All(d => _mentionsAny(d, ["Outline", "Primary"]))).IsTrue()
      .Because("no System-typed member, direct or as a generic argument, is reported");
  }

  [Test]
  public async Task PolymorphicModelDiscovery_SystemTypesAloneAreNotPolymorphic_AndALaterAbstractMemberIsFoundAsync() {
    var compilation = GeneratorTestHelper.CreateCompilation($$"""
      using System;
      using System.Collections.Generic;

      namespace Sample;

      public class Holder<T> {
        public T? Value { get; set; }
      }

      public abstract class Shape {
        public string Label { get; set; } = "";
      }

      public class Inner {
        public Shape? Outline { get; set; }
      }

      public class SystemOnly {
        {{SYSTEM_MEMBERS}}
      }

      public class SystemThenPolymorphic {
        {{SYSTEM_MEMBERS}}
        public List<Holder<Inner>> Nested { get; set; } = [];
      }
      """);

    await Assert.That(PolymorphicModelDiscovery.IsPolymorphic(compilation.GetTypeByMetadataName("Sample.SystemOnly"))).IsFalse();
    await Assert.That(PolymorphicModelDiscovery.IsPolymorphic(compilation.GetTypeByMetadataName("Sample.SystemThenPolymorphic"))).IsTrue();
  }

  private static List<string> _reportedMembers(IEnumerable<Microsoft.CodeAnalysis.Diagnostic> diagnostics, string id, string[] candidates) =>
    [.. diagnostics
      .Where(d => d.Id == id)
      .Select(d => d.GetMessage(CultureInfo.InvariantCulture))
      .SelectMany(m => candidates.Where(c => m.Contains($"'{c}'", StringComparison.Ordinal)))
      .Distinct(StringComparer.Ordinal)];

  private static bool _mentionsAny(Microsoft.CodeAnalysis.Diagnostic diagnostic, string[] names) {
    var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
    return names.Any(n => message.Contains($"'{n}'", StringComparison.Ordinal));
  }
}
