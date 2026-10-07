// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// That declaring a JSON converter on a perspective model is reported, because nothing consults it.
/// </summary>
/// <remarks>
/// <para>
/// A perspective's document is stored with <c>ComplexProperty().ToJson()</c> and read back by Entity
/// Framework's own materializer, which never consults a <c>[JsonConverter]</c>. Declaring one compiles,
/// runs, and does nothing.
/// </para>
/// <para>
/// This is not hypothetical. A consumer whose stored field changed from a number to a string declared a
/// converter to keep the pre-change rows readable, documented it as the fix, and shipped it. It had
/// never run: 4,034 rows stayed unreadable, and loading one threw, dropped the read model into drain
/// mode and froze the feature for every affected tenant. The silence is the defect — a changed stored
/// shape is what <c>[StoredForm]</c> is for, and nothing pointed there.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/DiagnosticDescriptors.cs</code-under-test>
public class DeclaredJsonConverterIgnoredTests {

  private static string _source(string body) => $$"""
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    {{body}}

    [WhizbangDbContext]
    public class ExportDbContext : DbContext {
      public ExportDbContext(DbContextOptions<ExportDbContext> options) : base(options) { }
    }
    """;

  private const string DECLARING_MODEL = """
    using System;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    public record ExportEvent : IEvent;

    public sealed class LegacyVersionConverter : JsonConverter<string?> {
      public override string? Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) => null;
      public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions o) { }
    }

    public record ExportModel {
      public string Id { get; init; } = "";

      [JsonConverter(typeof(LegacyVersionConverter))]
      public string? Version { get; init; }
    }

    public class ExportPerspective : IPerspectiveFor<ExportModel, ExportEvent> {
      public ExportModel Apply(ExportModel currentData, ExportEvent eventData) => currentData;
    }

    """;

  /// <summary>A declared converter is reported, naming the value and the converter.</summary>
  [Test]
  public async Task ADeclaredConverterIsReportedAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(_source(DECLARING_MODEL));

    var reported = result.Diagnostics.Where(d => d.Id == "WHIZ811").ToList();
    await Assert.That(reported).IsNotEmpty()
      .Because("declaring a converter that nothing consults must not be silent");

    var message = reported[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("Version")
      .Because("the author needs to know which value they declared it on");
    await Assert.That(message).Contains("LegacyVersionConverter");
    await Assert.That(message).Contains("StoredForm")
      .Because("a warning that does not name the supported alternative leaves the author stuck");
  }

  /// <summary>A model declaring none is not warned about.</summary>
  [Test]
  public async Task AModelWithNoConverterIsNotReportedAsync() {
    const string plain = """
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      public record PlainEvent : IEvent;

      public record PlainModel {
        public string Id { get; init; } = "";
        public string? Version { get; init; }
      }

      public class PlainPerspective : IPerspectiveFor<PlainModel, PlainEvent> {
        public PlainModel Apply(PlainModel currentData, PlainEvent eventData) => currentData;
      }

      """;

    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(_source(plain));
    await Assert.That(result.Diagnostics.Any(d => d.Id == "WHIZ811")).IsFalse()
      .Because("nothing was declared, so there is nothing to warn about");
  }

  /// <summary>It is a warning, not an error: the model still builds and still works for new rows.</summary>
  [Test]
  public async Task ItIsAWarningNotAnErrorAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(_source(DECLARING_MODEL));
    var reported = result.Diagnostics.First(d => d.Id == "WHIZ811");
    await Assert.That(reported.Severity).IsEqualTo(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
  }
}
