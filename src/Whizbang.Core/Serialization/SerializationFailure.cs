// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace Whizbang.Core.Serialization;

/// <summary>
/// The one test for "the serializer could not handle this envelope", shared by every path that falls back
/// when it cannot: <see cref="JsonException"/> for bytes that are not the contract,
/// <see cref="NotSupportedException"/> for a type no registered context describes, and
/// <see cref="InvalidOperationException"/> for metadata or content that contradicts itself (a resolver
/// answering for another type, an envelope naming another message).
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Serialization/SerializationFailureTests.cs</tests>
internal static class SerializationFailure {
  /// <summary>Whether <paramref name="ex"/> is a serialization failure the caller falls back from.</summary>
  internal static bool IsSerializationFailure(Exception ex) =>
    ex is JsonException or NotSupportedException or InvalidOperationException;
}
