// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.MultiStream;

/// <summary>
/// Works one stream of a <see cref="IMultiStreamCommand"/>. Called on the instance that owns the stream, under the
/// security scope the command was sent with, once per stream (and again only if the stream is re-offered).
/// </summary>
/// <typeparam name="TCommand">The command type this handler works.</typeparam>
/// <docs>fundamentals/dispatcher/multi-stream-commands#handlers</docs>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/MultiStreamCommandRegistrationTests.cs:InvokeAsync_ReadsThePayloadBackAndCallsTheHandlerForThatStreamAsync</tests>
public interface IMultiStreamCommandHandler<in TCommand> where TCommand : IMultiStreamCommand {
  /// <summary>
  /// Does the command's work for one stream. Throwing records an attempt that failed; the stream is retried up to the
  /// configured attempt limit and then recorded as failed with the exception's type and message.
  /// </summary>
  /// <param name="command">The command, as the caller sent it.</param>
  /// <param name="streamId">The one stream to work, owned by this instance.</param>
  /// <param name="cancellationToken">Canceled when the host stops; the stream is then re-offered.</param>
  ValueTask HandleStreamAsync(TCommand command, Guid streamId, CancellationToken cancellationToken);
}
