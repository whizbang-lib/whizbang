// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.CodeAnalysis;

namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a discovered event with stream key.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="EventType">Fully qualified event type name</param>
/// <param name="PropertyName">Name of the property or parameter marked with [StreamId]</param>
/// <param name="PropertyType">Fully qualified type of the stream key property</param>
/// <param name="IsPropertyValueType">True if the property type is a value type (struct)</param>
/// <param name="HasGenerate">True if [GenerateStreamId] applies to the event</param>
/// <param name="OnlyIfEmpty">True if [GenerateStreamId(OnlyIfEmpty = true)] is set</param>
/// <param name="IsPropertyInitOnly">True if the property has no settable set accessor</param>
/// <param name="PropertyLocation">Where the property is declared, for diagnostics</param>
/// <param name="InheritanceDepth">
/// How many base types the event has. The generated dispatch checks run deepest first, so a base type's check never
/// matches a derived instance ahead of the derived type's own check (#1893).
/// </param>
/// <param name="DeclinesInheritedGenerate">
/// True if the event declares its own [StreamId] and no [GenerateStreamId] while a base type's policy generates. It
/// gets an explicit "do not generate" policy case, so it does not inherit the base's [GenerateStreamId] (#1893).
/// </param>
/// <docs>extending/attributes/streamid</docs>
/// <tests>tests/Whizbang.Generators.Tests/StreamIdDerivedFirstGeneratorTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamIdInfoTests.cs:StreamIdInfo_ValueEquality_ComparesFieldsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamIdInfoTests.cs:StreamIdInfo_Constructor_SetsPropertiesAsync</tests>
public sealed record StreamIdInfo(
    string EventType,
    string PropertyName,
    string PropertyType,
    bool IsPropertyValueType,
    bool HasGenerate = false,
    bool OnlyIfEmpty = false,
    bool IsPropertyInitOnly = false,
    Location? PropertyLocation = null,
    int InheritanceDepth = 0,
    bool DeclinesInheritedGenerate = false
);

/// <summary>
/// Value type containing information about a discovered event without stream key.
/// Includes location for proper diagnostic reporting with suppression support.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="EventType">Fully qualified event type name</param>
/// <param name="Location">Source location of the event type declaration for diagnostic reporting</param>
public sealed record EventWithoutStreamIdInfo(
    string EventType,
    Location Location
);

/// <summary>
/// Value type containing information about a discovered command with stream ID.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="CommandType">Fully qualified command type name</param>
/// <param name="PropertyName">Name of the property marked with [StreamId]</param>
/// <param name="PropertyType">Fully qualified type of the stream ID property</param>
/// <param name="IsPropertyValueType">True if the property type is a value type (struct)</param>
/// <param name="HasGenerate">True if [GenerateStreamId] is present on the command</param>
/// <param name="OnlyIfEmpty">True if [GenerateStreamId(OnlyIfEmpty = true)] is set</param>
/// <param name="IsPropertyInitOnly">True if the property has no settable set accessor</param>
/// <param name="PropertyLocation">Where the property is declared, for diagnostics</param>
/// <param name="InheritanceDepth">
/// How many base types the command has. The generated dispatch checks run deepest first, so a base type's check never
/// matches a derived instance ahead of the derived type's own check (#1893).
/// </param>
/// <param name="DeclinesInheritedGenerate">
/// True if the command declares its own [StreamId] and no [GenerateStreamId] while a base type's policy generates. It
/// gets an explicit "do not generate" policy case, so it does not inherit the base's [GenerateStreamId] (#1893).
/// </param>
/// <docs>extending/attributes/streamid</docs>
/// <tests>tests/Whizbang.Generators.Tests/StreamIdDerivedFirstGeneratorTests.cs</tests>
public sealed record CommandStreamIdInfo(
    string CommandType,
    string PropertyName,
    string PropertyType,
    bool IsPropertyValueType,
    bool HasGenerate = false,
    bool OnlyIfEmpty = false,
    bool IsPropertyInitOnly = false,
    Location? PropertyLocation = null,
    int InheritanceDepth = 0,
    bool DeclinesInheritedGenerate = false
);
