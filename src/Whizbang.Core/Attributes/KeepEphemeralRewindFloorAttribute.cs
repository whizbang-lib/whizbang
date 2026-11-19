// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Attributes;

/// <summary>
/// Keeps a rewind floor for a perspective that applies only <see cref="EphemeralAttribute">ephemeral</see> events: it
/// snapshots on the ephemeral cadence, and its ephemeral bodies are reaped only once a snapshot covers them (#2295).
/// </summary>
/// <remarks>
/// <para>
/// Whether a perspective snapshots is decided by what it consumes. One that applies any durable event keeps its
/// snapshots, because they are either an optimization (durable only) or the only record of what reaped ephemeral events
/// contributed (mixed). One that applies only ephemeral events takes none by default: a snapshot there exists only to be
/// the floor of a rewind for a late event, the rewind grace window (300 seconds by default) is far shorter than the
/// history it would keep, and after the window the floor guards a case that cannot arise while it holds every body of
/// the stream back from the reaper. Its bodies are reaped once consumed and past their grace and TTL, and a late event
/// that would need a rewind below them is dropped and logged.
/// </para>
/// <para>
/// Mark the perspective with this attribute when it does need rewinds after the grace window: it then keeps the
/// behavior of an ephemeral perspective before #2295, one snapshot slot per stream and the snapshot coverage gate. On a
/// perspective that applies any durable event the attribute changes nothing.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events#snapshots-follow-what-a-read-model-consumes</docs>
/// <tests>tests/Whizbang.Generators.Tests/EphemeralRewindFloorGeneratorTests.cs</tests>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class KeepEphemeralRewindFloorAttribute : Attribute;
