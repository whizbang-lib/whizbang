// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.CodeAnalysis;

namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a discovered topic filter on a command.
/// This record uses value equality which is critical for incremental generator performance.
/// Sealed for performance optimization - allows better caching by the incremental generator.
/// </summary>
/// <param name="CommandType">Fully qualified command type name (e.g., "global::MyApp.Commands.CreateOrder")</param>
/// <param name="Filter">The topic filter string extracted from the attribute</param>
/// <param name="Location">The attribute's location, where WHIZ022 is reported (#1429). The output that reports
/// it already combines the compilation, so a location costs incremental caching nothing extra.</param>
/// <tests>tests/Whizbang.Generators.Tests/TopicFilterGeneratorTests.cs</tests>
internal sealed record TopicFilterInfo(
    string CommandType,
    string Filter,
    Location? Location = null
);
