// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Minting;

namespace Whizbang.Core.Component.Tests.CompositePublish.Foreign;

/// <summary>A composite in a namespace this service does not own: the owning services expand it, this one does not.</summary>
public sealed class CpForeignComposite : CompositeEventBase;
