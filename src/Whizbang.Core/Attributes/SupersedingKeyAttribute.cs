// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Attributes;

/// <summary>
/// Marks the property of a <see cref="SupersedingAttribute">superseding</see> event that names what it holds the state
/// of: a newer event supersedes an older one only on the same stream, of the same type, with the same key.
/// </summary>
/// <remarks>
/// <para>
/// The key is the property's value as invariant text (a string as is, a number or a <see cref="Guid"/> in its invariant
/// form, anything else by <see cref="object.ToString"/>), stamped when the event is stored. A
/// <see langword="null"/> value leaves the event out of superseding altogether. At most one property of a type may carry
/// the attribute (WHIZ136); on a record's primary-constructor parameter it marks the property of the same name.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/superseding-events#the-key</docs>
/// <tests>tests/Whizbang.Generators.Tests/MessageTypeCatalogGeneratorTests.cs:Generator_SupersedingEvent_StampsTheEntryAndRegistersTheKeyAsync</tests>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
public sealed class SupersedingKeyAttribute : Attribute;
