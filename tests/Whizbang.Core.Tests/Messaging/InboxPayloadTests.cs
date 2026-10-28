// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>Issue #1793: the size the inbox byte budgets charge for a payload.</summary>
[Category("Messaging")]
public sealed class InboxPayloadTests {
  [Test]
  public async Task Bytes_IsTheUtf8LengthOfThePayloadJsonAsync() {
    await Assert.That(InboxPayload.Bytes(JsonElement.Parse("""{"Name":"é"}"""))).IsEqualTo(13);
  }

  [Test]
  public async Task Bytes_OfAPayloadNeverSet_IsZeroAsync() {
    await Assert.That(InboxPayload.Bytes(default)).IsEqualTo(0);
  }
}
