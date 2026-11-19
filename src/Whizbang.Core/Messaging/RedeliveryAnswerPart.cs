// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// Which answer a redelivery bundle belongs to, and where in it (#2340), and whether that answer is to a request for a
/// whole type (#2341). The origin stamps it on every bundle of an answer it publishes through
/// <see cref="RedeliveryPump.BeginAnswer"/>. The receiver uses it to judge a whole-type answer only once every one of
/// its bundles has committed, whatever order they commit in: an answer's bundles can ride several broker sessions (one
/// per stream for a requester that does not accept bundles spanning streams, or one per scope), so its last bundle can
/// commit before the others.
/// </summary>
/// <param name="AnswerId">The answer's id, the same on every one of its bundles and different for every answer.</param>
/// <param name="BundleIndex">The bundle's place in the answer, counting from 0 in the order the origin published.</param>
/// <param name="BundleCount">How many bundles the answer has; set only on its last bundle, null on every other.</param>
/// <param name="WholeType">True when the answer is to a request for a whole type (it named no streams and no events).
/// Only such an answer can mark a type lane as one repair cannot change; an answer to a request for named streams says
/// nothing about the type's other streams.</param>
/// <docs>resilience/stream-integrity#a-whole-type-answer-is-judged-once-all-of-it-is-in</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/RedeliveryPumpWindowTests.AnswerParts.cs</tests>
public readonly record struct RedeliveryAnswerPart(Guid AnswerId, int BundleIndex, int? BundleCount, bool WholeType);
