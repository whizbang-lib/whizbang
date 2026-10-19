// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Minting;

namespace Whizbang.Core.Component.Tests.CompositePublish.Bundles;

/// <summary>A composite declared in another namespace than the events it carries, as a shared bundle type is.</summary>
public sealed class CpBundleComposite : CompositeEventBase;

/// <summary>An event of the bundle namespace, so its topic differs from the owned domain's.</summary>
public record CpBundleNote([property: StreamId] Guid StreamId) : Whizbang.Core.IEvent;
