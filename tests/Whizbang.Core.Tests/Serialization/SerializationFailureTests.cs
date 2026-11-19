// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>
/// <see cref="SerializationFailure.IsSerializationFailure"/> is what the consumer's custody body and the dead-letter
/// re-entry fall back on: each of the serializer's three refusals is a serialization failure, and nothing else is.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Serialization/SerializationFailure.cs</code-under-test>
public class SerializationFailureTests {
  [Test]
  [Arguments("json")]
  [Arguments("not-supported")]
  [Arguments("invalid-operation")]
  public async Task IsSerializationFailure_EachRefusalOfTheSerializer_IsAFailureAsync(string kind) {
    Exception ex = kind switch {
      "json" => new JsonException("bytes that are not the contract"),
      "not-supported" => new NotSupportedException("no context describes the type"),
      _ => new InvalidOperationException("metadata for another type"),
    };

    await Assert.That(SerializationFailure.IsSerializationFailure(ex)).IsTrue();
  }

  [Test]
  public async Task IsSerializationFailure_AnyOtherException_IsNotAsync() {
    await Assert.That(SerializationFailure.IsSerializationFailure(new TimeoutException("the store did not answer"))).IsFalse()
      .Because("a fault that is not the serializer's must surface rather than be read as an unwritable envelope");
  }
}
