// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Issue #2016: a positional record whose base record contributes members of its own (the framework's
/// <c>OpeningBalance</c>, or any abstract record with init members) has one public constructor, and it takes only the
/// positional parameters. The generated JSON context constructed such a type as <c>new T() { ... }</c>, which does not
/// compile (CS7036). It now passes the positional members to that constructor and sets the base's members in an
/// object initializer after it, wherever the type is reached: as a message, as a type a message carries, or as a
/// derived type of a polymorphic base.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/MessageJsonContextGenerator.cs</code-under-test>
[Category("SourceGenerators")]
[Category("JsonSerialization")]
public class MessageJsonContextPositionalRecordTests {
  private const string CONTEXT = "MessageJsonContext.g.cs";

  /// <summary>
  /// The context's facade references WhizbangIdJsonContext, which WhizbangIdGenerator emits, so both run.
  /// </summary>
  [RequiresAssemblyFiles]
  private static ImmutableArray<Diagnostic> _compileErrors(string source) =>
    GeneratorTestHelper.GetGeneratedCompilationErrors([new MessageJsonContextGenerator(), new WhizbangIdGenerator()], source);

  /// <summary>
  /// The object creator the context generates for <paramref name="type"/>, with its whitespace collapsed to single
  /// spaces so an assertion does not depend on the generator's indentation.
  /// </summary>
  private static string _creatorOf(string code, string type) {
    var start = code.IndexOf($"static args => new {type}(", StringComparison.Ordinal);
    var end = code.IndexOf("PropertyMetadataInitializer", start, StringComparison.Ordinal);
    return string.Join(' ', code[start..end].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_APositionalRecordDerivingFromOpeningBalance_CompilesAsync() {
    var errors = _compileErrors("""
      using Whizbang.Core.Lifecycle;
      namespace TestApp;
      public sealed record AccountOpened(decimal Balance) : OpeningBalance;
      """);

    await Assert.That(errors).IsEmpty()
      .Because("the positional constructor is the record's only public one, so a parameterless construction is CS7036");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_APositionalRecordDerivingFromOpeningBalance_PassesThePositionalMemberAndInitializesTheBaseAsync() {
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>("""
      using Whizbang.Core.Lifecycle;
      namespace TestApp;
      public sealed record AccountOpened(decimal Balance) : OpeningBalance;
      """);
    var code = GeneratorTestHelper.GetRequiredGeneratedSource(result, CONTEXT);
    var creator = _creatorOf(code, "global::TestApp.AccountOpened");

    await Assert.That(creator).StartsWith("static args => new global::TestApp.AccountOpened( (decimal)args[0] ) {")
      .Because("the positional member goes to the constructor");
    await Assert.That(creator).Contains("SuccessorStreamId = (global::System.Guid)args[");
    await Assert.That(creator).Contains("PredecessorStreamId = (global::System.Guid)args[");
    await Assert.That(creator).Contains("PredecessorFinalVersion = (long)args[")
      .Because("the base's init members follow the constructor in an object initializer");
  }

  /// <summary>
  /// A public constructor binds only when its parameters are the leading writable properties, in order: one taking
  /// more parameters than there are writable properties, or one whose parameter is not the first property, is passed
  /// over, and the parameterless constructor with an initializer is used.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ConstructorsThatDoNotTakeTheLeadingProperties_AreNotUsedAsync() {
    const string source = """
      using Whizbang.Core;
      namespace TestApp;
      public class Tallied : IEvent {
        public Tallied() { }
        public Tallied(int count, string note, bool flag) { Count = count; }
        public Tallied(string other) { }
        public int Count { get; set; }
      }
      """;
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);
    var creator = _creatorOf(GeneratorTestHelper.GetRequiredGeneratedSource(result, CONTEXT), "global::TestApp.Tallied");

    await Assert.That(creator).IsEqualTo("static args => new global::TestApp.Tallied() { Count = (int)args[0] },");
    await Assert.That(_compileErrors(source)).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_APositionalRecordWithAMemberContributingBase_CarriedByAMessage_CompilesAsync() {
    var errors = _compileErrors("""
      using System;
      using Whizbang.Core;
      namespace TestApp;
      public abstract record Stamped {
        public DateTimeOffset StampedAt { get; init; }
      }
      public sealed record Line(string Sku, int Quantity) : Stamped;
      public record LineAdded([property: StreamId] Guid OrderId, Line Line) : IEvent;
      """);

    await Assert.That(errors).IsEmpty()
      .Because("a type reached only through a message's property is constructed the same way as the message");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_APositionalDerivedTypeOfAPolymorphicBase_CompilesAsync() {
    var errors = _compileErrors("""
      using System;
      using System.Text.Json.Serialization;
      using Whizbang.Core;
      namespace TestApp;
      [JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
      [JsonDerivedType(typeof(Percentage), "percentage")]
      public abstract record Discount {
        public string Reason { get; init; } = "";
      }
      public sealed record Percentage(decimal Rate) : Discount;
      public record DiscountApplied([property: StreamId] Guid OrderId, Discount Discount) : IEvent;
      """);

    await Assert.That(errors).IsEmpty()
      .Because("a derived type found through [JsonDerivedType] is constructed the same way as the message");
  }
}
