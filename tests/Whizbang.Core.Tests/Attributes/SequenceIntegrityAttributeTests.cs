// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Attributes;

/// <summary>
/// The attribute is read into the message type catalog at compile time, so nothing in the library constructs it at run
/// time; these tests pin what it carries for a consumer that reads it directly.
/// </summary>
[Category("Core")]
[Category("Attributes")]
public class SequenceIntegrityAttributeTests {
  [Test]
  public async Task Constructor_WithAModeOnly_CarriesTheMode_AndNoHoldTimeOfItsOwnAsync() {
    var attribute = new SequenceIntegrityAttribute(SequenceIntegrity.UntilStamped);

    await Assert.That(attribute.Mode).IsEqualTo(SequenceIntegrity.UntilStamped);
    await Assert.That(attribute.HoldMilliseconds).IsEqualTo(0);
  }

  [Test]
  public async Task Constructor_WithAHoldTime_CarriesBothAsync() {
    var attribute = new SequenceIntegrityAttribute(SequenceIntegrity.Bounded, 2500);

    await Assert.That(attribute.Mode).IsEqualTo(SequenceIntegrity.Bounded);
    await Assert.That(attribute.HoldMilliseconds).IsEqualTo(2500);
  }
}
