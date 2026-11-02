// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Attributes;

/// <summary>
/// The form an event type's metadata takes in the event store row (<c>wh_event_body.metadata</c>).
/// </summary>
/// <docs>data/event-store#stored-metadata</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/StoredMetadataRegistryTests.cs</tests>
public enum StoredMetadataForm {
  /// <summary>
  /// The form <c>Whizbang:EventStore:Metadata:WriteForm</c> chooses (by default the slim row with its full form staged).
  /// On a type that derives from one marked <see cref="Full"/>, it takes the type back to that default.
  /// </summary>
  Default = 0,

  /// <summary>
  /// The row keeps the event's full metadata (every hop as written) for good, whatever the write form.
  /// </summary>
  Full = 1,
}

/// <summary>
/// Chooses the form of an event type's stored metadata. <c>[StoredMetadata(StoredMetadataForm.Full)]</c> keeps the full
/// hops in the event row for the type, for every type deriving from it, and for every type implementing an interface
/// that carries it.
/// </summary>
/// <remarks>
/// <para>
/// Precedence, highest first: debug mode (every row is full), the configuration list
/// <c>Whizbang:EventStore:Metadata:FullFor</c>, this attribute, then the write form. The nearest declaration wins: the
/// type's own attribute, then its base classes from the nearest, then its interfaces.
/// </para>
/// <para>
/// The attribute is read at build time by a source generator, never by reflection, and the resolved list is published
/// to <c>wh_settings</c> (<c>metadata_full_for</c>), where the write paths match it.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [StoredMetadata(StoredMetadataForm.Full)]
/// public record PaymentCaptured(Guid PaymentId, decimal Amount) : IEvent;
/// </code>
/// </example>
/// <param name="form">The form the type's rows take.</param>
/// <docs>data/event-store#keeping-an-event-type-full</docs>
/// <tests>tests/Whizbang.Generators.Tests/StoredMetadataRegistryGeneratorTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/StoredMetadataRegistryTests.cs:TheAttribute_CarriesTheFormItIsGivenAsync</tests>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, AllowMultiple = false, Inherited = true)]
public sealed class StoredMetadataAttribute(StoredMetadataForm form) : Attribute {
  /// <summary>The form the type's rows take.</summary>
  public StoredMetadataForm Form { get; } = form;
}
