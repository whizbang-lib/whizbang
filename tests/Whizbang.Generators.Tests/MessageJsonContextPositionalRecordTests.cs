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

    await Assert.That(code).Contains(
      "static args => new global::TestApp.AccountOpened(" + Environment.NewLine
      + "          (decimal)args[0]" + Environment.NewLine
      + "      ) {" + Environment.NewLine
      + "          SuccessorStreamId = (global::System.Guid)args[1]," + Environment.NewLine
      + "          PredecessorStreamId = (global::System.Guid)args[2]," + Environment.NewLine
      + "          PredecessorFinalVersion = (long)args[3]" + Environment.NewLine
      + "      },")
      .Because("the positional member goes to the constructor, and the base's init members follow in an initializer");
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
