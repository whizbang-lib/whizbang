// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.MultiStream;

/// <summary>
/// A multi-stream command as recorded by the instance that received it: what to run, for which streams, and under which
/// security scope.
/// </summary>
/// <param name="RunId">The command's <see cref="IMultiStreamCommand.RunId"/>.</param>
/// <param name="CommandType">The command's type name, as <see cref="TypeNameFormatter.Format"/> renders it; the key an
/// instance finds the command's <see cref="IMultiStreamCommandBinding"/> by.</param>
/// <param name="Payload">The command serialized to JSON.</param>
/// <param name="StreamIds">The streams to work, each once, in the order the caller first named them.</param>
/// <param name="Scope">The security scope the command was sent under, as a serialized scope delta, or null when it was
/// sent with none.</param>
/// <docs>fundamentals/dispatcher/multi-stream-commands#intake</docs>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/MultiStreamCommandIntakeTests.cs:HandleAsync_RecordsOneRunCarryingTheCommandAndItsStreamsAsync</tests>
public sealed record MultiStreamRun(Guid RunId, string CommandType, string Payload, IReadOnlyList<Guid> StreamIds, string? Scope);
