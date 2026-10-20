// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for <see cref="MessageProcessingOptions"/> default values and property setters.
/// </summary>
/// <tests>src/Whizbang.Core/Workers/MessageProcessingOptions.cs</tests>
/// <docs>messaging/transports/transport-consumer#message-processing-options</docs>
public class MessageProcessingOptionsTests {

  [Test]
  public async Task DefaultValues_MatchDocumentedDefaultsAsync() {
    // Arrange & Act
    var options = new MessageProcessingOptions();

    // Assert — all defaults match documented values
    await Assert.That(options.MaxConcurrentMessages).IsEqualTo(40)
      .Because("Default leaves headroom in a 100-connection pool");
  }

  [Test]
  public async Task CustomValues_OverrideDefaultsAsync() {
    // Arrange & Act
    var options = new MessageProcessingOptions { MaxConcurrentMessages = 80 };

    // Assert
    await Assert.That(options.MaxConcurrentMessages).IsEqualTo(80);
  }

  [Test]
  public async Task MaxConcurrentMessages_ZeroDisablesSemaphoreAsync() {
    // Arrange & Act — 0 means disabled per documentation
    var options = new MessageProcessingOptions { MaxConcurrentMessages = 0 };

    // Assert
    await Assert.That(options.MaxConcurrentMessages).IsEqualTo(0);
  }
}
