// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>Types the generator registers through this assembly's module initializer.</summary>
[StoredMetadata(StoredMetadataForm.Full)]
public sealed record StoredMetadataFullProbe;

/// <summary>Kept full through its interface.</summary>
public sealed record StoredMetadataFullByInterfaceProbe : IStoredMetadataFullProbeMarker;

/// <summary>The marker interface that keeps its implementers full.</summary>
[StoredMetadata(StoredMetadataForm.Full)]
public interface IStoredMetadataFullProbeMarker;

/// <summary>Opts back to the write form although its interface keeps it full: its own declaration is nearer.</summary>
[StoredMetadata(StoredMetadataForm.Default)]
public sealed record StoredMetadataDefaultProbe : IStoredMetadataFullProbeMarker;

/// <summary>
/// #1725: the registry of event types kept full by <c>[StoredMetadata(StoredMetadataForm.Full)]</c>, filled by the
/// generator at module load and read by name, so the framework reads no attribute by reflection.
/// </summary>
/// <docs>data/event-store#keeping-an-event-type-full</docs>
/// <code-under-test>src/Whizbang.Core/Messaging/StoredMetadataRegistry.cs</code-under-test>
[Category("Messaging")]
public class StoredMetadataRegistryTests {
  [Test]
  public async Task RegisterFull_ThenFullTypeNames_ListsItOnceAsync() {
    StoredMetadataRegistry.RegisterFull("Zz.StoredMetadataRegistryTests.Registered");
    StoredMetadataRegistry.RegisterFull("Zz.StoredMetadataRegistryTests.Registered");

    var names = StoredMetadataRegistry.FullTypeNames();

    await Assert.That(names.Count(n => n == "Zz.StoredMetadataRegistryTests.Registered")).IsEqualTo(1);
    await Assert.That(names).IsOrderedBy(n => n, StringComparer.Ordinal);
  }

  [Test]
  [Arguments(StoredMetadataForm.Full)]
  [Arguments(StoredMetadataForm.Default)]
  public async Task TheAttribute_CarriesTheFormItIsGivenAsync(StoredMetadataForm form) {
    var attribute = new StoredMetadataAttribute(form);

    await Assert.That(attribute.Form).IsEqualTo(form)
      .Because("the generator reads the form from the attribute's constructor argument, which Form exposes");
  }

  [Test]
  public async Task RegisterFull_ABlankName_ThrowsAsync() {
    await Assert.That(() => StoredMetadataRegistry.RegisterFull(" ")).Throws<ArgumentException>();
  }

  [Test]
  public async Task TheGeneratedRegistration_ListsTheAttributedTestTypesAsync() {
    var names = StoredMetadataRegistry.FullTypeNames();

    await Assert.That(names).Contains(typeof(StoredMetadataFullProbe).FullName!);
    await Assert.That(names).Contains(typeof(StoredMetadataFullByInterfaceProbe).FullName!)
      .Because("an interface carrying the attribute keeps its implementers full");
    await Assert.That(names).DoesNotContain(typeof(StoredMetadataDefaultProbe).FullName!)
      .Because("the type's own Default is nearer than its interface's Full");
    await Assert.That(names).DoesNotContain(typeof(IStoredMetadataFullProbeMarker).FullName!)
      .Because("an interface is never an event's stored type");
  }
}
