// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Whizbang.Core.Attributes;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tag registrations for the coalesce tests. Shared by the unit and component projects (one source,
/// compiled into both): CoalesceGroupResolverTests and CoalesceShipWorkerTests build their
/// resolvers from the same registrations.
/// </summary>
internal static class CoalesceTestTags {
  internal static MessageTagRegistration TagRegistration(Type messageType, string tag) => new() {
    MessageType = messageType,
    AttributeType = typeof(SignalTagAttribute),
    Tag = tag,
    PayloadBuilder = _ => JsonSerializer.SerializeToElement(new { }),
    AttributeFactory = () => new SignalTagAttribute { Tag = tag }
  };
}
