// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Tests.Helpers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Every form of a type's name is rendered once per type and kept (#2548). The stored form and the assembly-qualified
/// form are built on every outbox and inbox write, and the runtime keeps a type's names in a cache it holds weakly (the
/// assembly-qualified name is rebuilt on every read), so each read allocated, and again after every collection. With the
/// forms kept, a read after the first allocates nothing, even right after a collection; the forms are exactly what the
/// type's own names give, for every shape of type, and the same after a collection as before.
/// </summary>
/// <code-under-test>src/Whizbang.Core/TypeNameFormatter.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/EventTypeMatchingHelper.cs</code-under-test>
[NotInParallel("AllocationMeasurement")]
public class TypeNameFormatterCachingTests {
  private const int ITERATIONS = 1_000;

  public enum Form {
    Format,
    AssemblyQualifiedName,
    AssemblyQualifiedNameOrNull,
    AssemblyQualifiedNameOrDisplay,
    DisplayName,
    SimpleName,
    ForLog,
    FormatClrTypeName,
    TryFormatClrTypeName,
    FormatOrDisplay,
  }

  /// <summary>A nested type, named through its declaring type.</summary>
  public sealed record NestedNamingProbe;

  /// <summary>A generic type whose argument is nested.</summary>
  /// <typeparam name="T">The argument.</typeparam>
  /// <param name="Value">A value of the argument type.</param>
  public sealed record GenericNamingProbe<T>(T? Value);

  [Test]
  [Arguments(Form.Format)]
  [Arguments(Form.AssemblyQualifiedName)]
  [Arguments(Form.AssemblyQualifiedNameOrNull)]
  [Arguments(Form.AssemblyQualifiedNameOrDisplay)]
  [Arguments(Form.DisplayName)]
  [Arguments(Form.SimpleName)]
  [Arguments(Form.ForLog)]
  [Arguments(Form.FormatClrTypeName)]
  [Arguments(Form.TryFormatClrTypeName)]
  [Arguments(Form.FormatOrDisplay)]
  public async Task EveryForm_AfterTheFirstRead_AllocatesNothingEvenAfterACollectionAsync(Form form) {
    var type = typeof(GenericNamingProbe<NestedNamingProbe>);
    _ = _render(form, type);

    var before = GC.GetAllocatedBytesForCurrentThread();
    GC.Collect();
    for (var i = 0; i < ITERATIONS; i++) {
      _ = _render(form, type);
    }
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

    await Assert.That(allocated).IsEqualTo(0L)
      .Because($"{form} is read on per-message paths; it must be rendered once per type, not on each read or after each collection");
  }

  public static IEnumerable<Func<Type>> NamedTypes() {
    yield return () => typeof(IntegrityCheckpoint);
    yield return () => typeof(NestedNamingProbe);
    yield return () => typeof(GenericNamingProbe<NestedNamingProbe>);
    yield return () => typeof(Dictionary<string, List<NestedNamingProbe>>);
    yield return () => typeof(List<>);
    yield return () => typeof(NestedNamingProbe[]);
    yield return () => typeof(int);
    yield return () => typeof(string);
  }

  /// <summary>Each form is what the type's own names give, before and after a collection.</summary>
  [Test]
  [MethodDataSource(nameof(NamedTypes))]
  public async Task EveryForm_OfANamedType_IsWhatTheTypesOwnNamesGiveAsync(Type type) {
    for (var pass = 0; pass < 2; pass++) {
      await Assert.That(TypeNameFormatter.Format(type)).IsEqualTo(_expectedFormat(type));
      await Assert.That(TypeNameFormatter.AssemblyQualifiedName(type)).IsEqualTo(type.AssemblyQualifiedName);
      await Assert.That(TypeNameFormatter.AssemblyQualifiedNameOrNull(type)).IsEqualTo(type.AssemblyQualifiedName);
      await Assert.That(TypeNameFormatter.AssemblyQualifiedNameOrDisplay(type)).IsEqualTo(type.AssemblyQualifiedName);
      await Assert.That(TypeNameFormatter.DisplayName(type)).IsEqualTo(type.FullName);
      await Assert.That(TypeNameFormatter.SimpleName(type)).IsEqualTo(type.Name);
      await Assert.That(TypeNameFormatter.ForLog(type)).IsEqualTo(type.Name);
      await Assert.That(TypeNameFormatter.NamespaceOf(type)).IsEqualTo(type.Namespace);
      await Assert.That(TypeNameFormatter.FormatClrTypeName(type)).IsEqualTo(type.FullName);
      await Assert.That(TypeNameFormatter.TryFormatClrTypeName(type, out var clr)).IsTrue();
      await Assert.That(clr).IsEqualTo(type.FullName);
      await Assert.That(TypeNameFormatter.FormatOrDisplay(type)).IsEqualTo(_expectedFormat(type));
      GC.Collect();
    }
  }

  /// <summary>
  /// The stored form carries no version at any depth, while the assembly-qualified form keeps every version: the two
  /// stay distinct for a closed generic whose argument comes from another assembly.
  /// </summary>
  [Test]
  public async Task TheStoredForm_IsUnversioned_AndTheAssemblyQualifiedForm_IsVersionedAsync() {
    var type = typeof(Dictionary<string, List<NestedNamingProbe>>);

    await Assert.That(TypeNameFormatter.Format(type)).DoesNotContain("Version=");
    await Assert.That(TypeNameFormatter.AssemblyQualifiedName(type)).Contains("Version=");
  }

  /// <summary>
  /// A generic parameter has a simple name and nothing more: the forms that need a full or assembly-qualified name
  /// refuse it as they always have, and the forms with a fallback fall back to its simple name.
  /// </summary>
  [Test]
  public async Task AGenericParameter_KeepsEveryFormsRefusalAndFallbackAsync() {
    var parameter = typeof(List<>).GetGenericArguments()[0];

    for (var pass = 0; pass < 2; pass++) {
      await Assert.That(() => TypeNameFormatter.Format(parameter)).Throws<InvalidOperationException>()
        .WithMessage("Type T does not have a FullName");
      await Assert.That(() => TypeNameFormatter.AssemblyQualifiedName(parameter)).Throws<InvalidOperationException>()
        .WithMessage("Type T does not have an assembly-qualified name");
      await Assert.That(() => TypeNameFormatter.FormatClrTypeName(parameter)).Throws<InvalidOperationException>()
        .WithMessage("Type T does not have a FullName");
      await Assert.That(TypeNameFormatter.AssemblyQualifiedNameOrNull(parameter)).IsNull();
      await Assert.That(TypeNameFormatter.AssemblyQualifiedNameOrDisplay(parameter)).IsEqualTo("T");
      await Assert.That(TypeNameFormatter.DisplayName(parameter)).IsEqualTo("T");
      await Assert.That(TypeNameFormatter.FormatOrDisplay(parameter)).IsEqualTo("T");
      await Assert.That(TypeNameFormatter.TryFormatClrTypeName(parameter, out var clr)).IsFalse();
      await Assert.That(clr).IsNull();
      GC.Collect();
    }
  }

  /// <summary>
  /// The inbox's fallback classification asks whether a received type name is one of the known event types, on every
  /// such receive. It matches the versioned name, the unversioned name and the normalized name as before, and the
  /// collections cost it nothing.
  /// </summary>
  [Test]
  public async Task IsEventType_MatchesEveryFormAsBefore_AndReadsNoNameAfterACollectionAsync() {
    Type[] events = [typeof(NestedNamingProbe), typeof(GenericNamingProbe<NestedNamingProbe>)];
    var nested = typeof(NestedNamingProbe);
    var unversioned = $"{nested.FullName}, {nested.Assembly.GetName().Name}";

    await Assert.That(EventTypeMatchingHelper.IsEventType(nested.AssemblyQualifiedName!, events)).IsTrue();
    await Assert.That(EventTypeMatchingHelper.IsEventType(unversioned, events)).IsTrue();
    await Assert.That(EventTypeMatchingHelper.IsEventType($"{unversioned}, Version=9.9.9.9", events)).IsTrue();
    await Assert.That(EventTypeMatchingHelper.IsEventType("Elsewhere.NotAnEvent, Elsewhere", events)).IsFalse();

    var extra = WeakNameCacheProbe.ExtraBytesAfterCollections(
      () => _ = EventTypeMatchingHelper.IsEventType("Elsewhere.NotAnEvent, Elsewhere", events));
    await Assert.That(extra).IsEqualTo(0L);
  }

  /// <summary>The lookup a perspective resolves stored event names through keys every form as before; a type with no
  /// full name is keyed by its simple name only.</summary>
  [Test]
  public async Task BuildTypeLookup_KeysEveryFormAsBeforeAsync() {
    var nested = typeof(NestedNamingProbe);
    var parameter = typeof(List<>).GetGenericArguments()[0];

    var lookup = EventTypeMatchingHelper.BuildTypeLookup([nested, parameter]);

    await Assert.That(lookup[$"{nested.FullName}, {nested.Assembly.GetName().Name}"]).IsEqualTo(nested);
    await Assert.That(lookup[nested.FullName!]).IsEqualTo(nested);
    await Assert.That(lookup[nested.Name]).IsEqualTo(nested);
    await Assert.That(lookup["T"]).IsEqualTo(parameter);
    await Assert.That(lookup).Count().IsEqualTo(4);
  }

  private static string _expectedFormat(Type type) =>
    EventTypeMatchingHelper.NormalizeTypeName($"{type.FullName}, {type.Assembly.GetName().Name}");

  private static string? _render(Form form, Type type) => form switch {
    Form.Format => TypeNameFormatter.Format(type),
    Form.AssemblyQualifiedName => TypeNameFormatter.AssemblyQualifiedName(type),
    Form.AssemblyQualifiedNameOrNull => TypeNameFormatter.AssemblyQualifiedNameOrNull(type),
    Form.AssemblyQualifiedNameOrDisplay => TypeNameFormatter.AssemblyQualifiedNameOrDisplay(type),
    Form.DisplayName => TypeNameFormatter.DisplayName(type),
    Form.SimpleName => TypeNameFormatter.SimpleName(type),
    Form.ForLog => TypeNameFormatter.ForLog(type),
    Form.FormatClrTypeName => TypeNameFormatter.FormatClrTypeName(type),
    Form.TryFormatClrTypeName => TypeNameFormatter.TryFormatClrTypeName(type, out var name) ? name : null,
    _ => TypeNameFormatter.FormatOrDisplay(type),
  };
}
