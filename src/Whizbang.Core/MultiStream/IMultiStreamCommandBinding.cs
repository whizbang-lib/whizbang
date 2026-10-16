// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;

namespace Whizbang.Core.MultiStream;

/// <summary>
/// What an instance knows about one multi-stream command type: its stored name, how to read its payload back and call
/// its handler for one stream, and how to register its intake. One is registered per command type by
/// <see cref="MultiStreamCommandServiceCollectionExtensions.AddMultiStreamCommandHandler{TCommand, THandler}"/>; generic
/// over the command at compile time, so nothing is resolved by reflection.
/// </summary>
/// <docs>fundamentals/dispatcher/multi-stream-commands#registration</docs>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/MultiStreamCommandRegistrationTests.cs:AddMultiStreamCommandHandler_RegistersOneBindingNamedForTheCommandTypeAsync</tests>
public interface IMultiStreamCommandBinding {
  /// <summary>The command's type name, as recorded on its runs.</summary>
  string CommandType { get; }

  /// <summary>Reads <paramref name="payload"/> back into the command and calls its handler for <paramref name="streamId"/>.</summary>
  /// <param name="services">The scope to resolve the handler from.</param>
  /// <param name="payload">The run's stored payload.</param>
  /// <param name="streamId">The stream to work.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <exception cref="InvalidOperationException">The payload reads back as nothing.</exception>
  ValueTask InvokeAsync(IServiceProvider services, string payload, Guid streamId, CancellationToken cancellationToken);

  /// <summary>Registers the command's intake receptor at the stages a receptor without <c>[FireAt]</c> runs at.</summary>
  /// <param name="registry">The receptor registry to register into.</param>
  /// <param name="services">The root provider the intake resolves its store and logger from.</param>
  void RegisterIntake(IReceptorRegistry registry, IServiceProvider services);
}
