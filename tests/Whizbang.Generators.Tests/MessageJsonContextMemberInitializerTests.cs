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
/// Issue #2374: a member the document has no key for keeps its initializer. The generated context assigned every
/// writable member from the creator's arguments, and System.Text.Json passes a missing key's slot as the type's
/// default, so <c>= []</c> read as null and <c>= true</c> as false. Only the members a construction must set (the
/// constructor's arguments, <see langword="required"/> members, members required for reading, and init-only members
/// whose accessor cannot be named) are still passed to it; every other member is set after construction through its
/// setter, an init-only one through a generated <c>[UnsafeAccessor]</c>, and only when the document holds its key.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/MessageJsonContextGenerator.cs</code-under-test>
/// <tests>tests/Whizbang.Core.Tests/Serialization/MemberInitializerReadTests.cs</tests>
/// <docs>fundamentals/events/event-upcasting#a-new-field-with-a-fixed-default</docs>
[Category("SourceGenerators")]
[Category("JsonSerialization")]
public class MessageJsonContextMemberInitializerTests {
  private const string CONTEXT = "MessageJsonContext.g.cs";

  [RequiresAssemblyFiles]
  private static ImmutableArray<Diagnostic> _compileErrors(string source) =>
    GeneratorTestHelper.GetGeneratedCompilationErrors([new MessageJsonContextGenerator(), new WhizbangIdGenerator()], source);

  [RequiresAssemblyFiles]
  private static string _generated(string source) =>
    GeneratorTestHelper.GetRequiredGeneratedSource(GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source), CONTEXT);

  /// <summary>The object-info initializer the context generates for <paramref name="type"/>, whitespace collapsed.</summary>
  private static string _objectInfoOf(string code, string type) {
    var start = code.IndexOf($"new JsonObjectInfoValues<{type}> {{", StringComparison.Ordinal);
    var end = code.IndexOf("};", start, StringComparison.Ordinal);
    return string.Join(' ', code[start..end].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
  }

  private const string EVENT = """
    using System;
    using System.Collections.Generic;
    using Whizbang.Core;
    namespace TestApp;
    public abstract class BaseEvent : IEvent {
      [StreamId] public Guid StreamId { get; init; }
      public string Source { get; init; } = "unknown";
    }
    public sealed class ItemsExcluded : BaseEvent {
      public IReadOnlyList<Guid> ExcludedIds { get; init; } = [];
      public bool NotifyOnCreate { get; init; } = true;
      public int Limit { get; set; } = 25;
    }
    """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_AMessageWithoutRequiredMembers_IsBuiltByItsParameterlessConstructorAsync() {
    var objectInfo = _objectInfoOf(_generated(EVENT), "global::TestApp.ItemsExcluded");

    await Assert.That(objectInfo).Contains("ObjectCreator = static () => new global::TestApp.ItemsExcluded(),")
      .Because("no member has to be set at construction, so a missing key never reaches an assignment");
    await Assert.That(objectInfo).DoesNotContain("ObjectWithParameterizedConstructorCreator");
    await Assert.That(objectInfo).DoesNotContain("ConstructorParameterMetadataInitializer");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_AnInitOnlyMember_IsSetThroughAnAccessorOnTheTypeThatDeclaresItAsync() {
    var code = _generated(EVENT);

    await Assert.That(code).Contains("Name = \"set_ExcludedIds\")]");
    await Assert.That(code).Contains(
      "private static extern void InitAccessorFor_TestApp_ItemsExcluded_ExcludedIds(global::TestApp.ItemsExcluded target, ");
    await Assert.That(code).Contains(
      "private static extern void InitAccessorFor_TestApp_ItemsExcluded_Source(global::TestApp.BaseEvent target, string value);")
      .Because("an accessor is looked up on the exact type that declares the member, so an inherited one names its base");
    await Assert.That(code).Contains(
      "static (obj, value) => InitAccessorFor_TestApp_ItemsExcluded_Source((global::TestApp.BaseEvent)obj, value!)");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ASettableMember_IsSetThroughItsSetterAsync() {
    var code = _generated(EVENT);

    await Assert.That(code).Contains("(obj, value) => ((global::TestApp.ItemsExcluded)obj).Limit = value!");
    await Assert.That(code).DoesNotContain("Limit = (int)args[")
      .Because("a settable member is no longer also a constructor argument");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ARequiredMember_StaysInTheInitializer_AndTheMemberBesideItDoesNotAsync() {
    var objectInfo = _objectInfoOf(_generated("""
      using Whizbang.Core;
      namespace TestApp;
      public sealed class Rename : ICommand {
        public required string Name { get; init; }
        public int Attempts { get; init; } = 3;
      }
      """), "global::TestApp.Rename");

    await Assert.That(objectInfo).Contains("static args => new global::TestApp.Rename() { Name = (string)args[0] }")
      .Because("a construction must set a required member, or it does not compile");
    await Assert.That(objectInfo).DoesNotContain("Attempts");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_APositionalRecordWithARequiredMember_PassesBothToTheCreatorAsync() {
    var objectInfo = _objectInfoOf(_generated("""
      using System;
      using Whizbang.Core;
      namespace TestApp;
      public sealed record Quoted([property: StreamId] Guid OrderId) : IEvent {
        public required string Currency { get; init; }
        public int Decimals { get; init; } = 2;
      }
      """), "global::TestApp.Quoted");

    await Assert.That(objectInfo).Contains(
      "static args => new global::TestApp.Quoted( (global::System.Guid)args[0] ) { Currency = (string)args[1] }")
      .Because("the constructor takes the positional member and the initializer sets the required one after it");
    await Assert.That(objectInfo).DoesNotContain("Decimals");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_AnInitOnlyMemberOfAGenericBase_StaysInTheInitializerAsync() {
    var code = _generated("""
      using System;
      using Whizbang.Core;
      namespace TestApp;
      public abstract class Envelope<T> : IEvent {
        [StreamId] public Guid StreamId { get; init; }
        public T? Body { get; init; }
      }
      public sealed class Wrapped : Envelope<string> {
        public int Rank { get; init; } = 1;
      }
      """);
    var objectInfo = _objectInfoOf(code, "global::TestApp.Wrapped");

    await Assert.That(objectInfo).Contains("Body = (string?)args[")
      .Because("an accessor cannot name a generic declaring type without its type parameters, so it is set as before");
    await Assert.That(objectInfo).DoesNotContain("Rank =");
    await Assert.That(code).Contains("InitAccessorFor_TestApp_Wrapped_Rank(global::TestApp.Wrapped target, int value);");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_EveryShape_CompilesAsync() {
    var errors = _compileErrors(EVENT + """

      public sealed record Priced([property: StreamId] Guid OrderId) : IEvent {
        public string Currency { get; init; } = "USD";
      }
      public sealed class Rename : ICommand {
        public required string Name { get; init; }
        public int Attempts { get; init; } = 3;
      }
      public sealed record Quoted([property: StreamId] Guid QuoteId) : IEvent {
        public required string Currency { get; init; }
        public int Decimals { get; init; } = 2;
      }
      """);

    await Assert.That(errors).IsEmpty();
  }
}
