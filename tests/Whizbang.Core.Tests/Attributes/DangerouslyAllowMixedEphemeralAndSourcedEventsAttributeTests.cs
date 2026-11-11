// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Attributes;

namespace Whizbang.Core.Tests.Attributes;

/// <summary>
/// #2255: the attribute's <see cref="DangerouslyAllowMixedEphemeralAndSourcedEventsAttribute.Streams"/> defaults to
/// <see cref="MixedStreams.Refuse"/>, so the attribute alone changes nothing at run time, and carries
/// <see cref="MixedStreams.Allow"/> when set. The generator reads the property from syntax; this reads it from the type.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#allowing-mixed-streams</docs>
/// <code-under-test>src/Whizbang.Core/Attributes/DangerouslyAllowMixedEphemeralAndSourcedEventsAttribute.cs</code-under-test>
public class DangerouslyAllowMixedEphemeralAndSourcedEventsAttributeTests {
  [Test]
  public async Task Streams_DefaultsToRefuseAsync() {
    var attribute = new DangerouslyAllowMixedEphemeralAndSourcedEventsAttribute();

    await Assert.That(attribute.Streams).IsEqualTo(MixedStreams.Refuse)
      .Because("the attribute without the property must keep a stream from mixing, as before #2255");
  }

  [Test]
  public async Task Streams_CarriesAllow_WhenSetAsync() {
    var attribute = new DangerouslyAllowMixedEphemeralAndSourcedEventsAttribute { Streams = MixedStreams.Allow };

    await Assert.That(attribute.Streams).IsEqualTo(MixedStreams.Allow);
  }
}
