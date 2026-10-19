// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Commands.System;
using Whizbang.Core.MultiStream;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Rebuilds one stream of a <see cref="RebuildPerspectiveStreamsCommand"/>, on the instance that owns it. The rebuild
/// goes through the perspective's runner, which takes the same in-process apply lock as the stream's live work, so on its
/// owner the rebuild and live work for one stream never interleave (#1357). A rebuild that does not succeed throws, so
/// the stream's outcome is a failed attempt with the rebuild's error.
/// </summary>
/// <param name="rebuilder">The perspective rebuilder.</param>
/// <docs>fundamentals/perspectives/perspectives#rebuild</docs>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/RebuildPerspectiveStreamsHandlerTests.cs:HandleStreamAsync_RebuildsThatOneStreamUnderTheRebuildsOriginAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/RebuildPerspectiveStreamsHandlerTests.cs:HandleStreamAsync_ARebuildThatDidNotSucceed_ThrowsWithItsErrorAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRebuilderIntegrationTests.cs:RebuildCommand_NamingAStream_IsRebuiltByTheStreamsOwnerThroughARunAsync</tests>
public sealed class RebuildPerspectiveStreamsHandler(IPerspectiveRebuilder rebuilder) : IMultiStreamCommandHandler<RebuildPerspectiveStreamsCommand> {
  private readonly IPerspectiveRebuilder _rebuilder = rebuilder ?? throw new ArgumentNullException(nameof(rebuilder));

  /// <inheritdoc/>
  /// <exception cref="InvalidOperationException">The stream's rebuild did not succeed.</exception>
  public async ValueTask HandleStreamAsync(RebuildPerspectiveStreamsCommand command, Guid streamId, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(command);
    // As one stream of the run: the run reports once when it completes, so the stream announces nothing of its own
    var result = await _rebuilder.RebuildStreamOfRunAsync(command.PerspectiveName, streamId, command.Origin, cancellationToken).ConfigureAwait(false);
    if (!result.Success) {
      throw new InvalidOperationException($"Rebuilding stream {streamId} of {command.PerspectiveName} failed: {result.Error}");
    }
  }
}
