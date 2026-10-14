// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.MultiStream;

/// <summary>The binding for one command type. See <see cref="IMultiStreamCommandBinding"/>.</summary>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <docs>fundamentals/dispatcher/multi-stream-commands#registration</docs>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/MultiStreamCommandRegistrationTests.cs:InvokeAsync_ReadsThePayloadBackAndCallsTheHandlerForThatStreamAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/MultiStreamCommandRegistrationTests.cs:InvokeAsync_APayloadThatReadsAsNull_ThrowsAsync</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/MultiStream/MultiStreamCommandReceptorRegistrarTests.cs:StartAsync_RegistersTheIntakeAtTheDefaultReceptorStagesAsync</tests>
public sealed class MultiStreamCommandBinding<TCommand> : IMultiStreamCommandBinding where TCommand : IMultiStreamCommand {
  internal static readonly string TYPE_NAME = TypeNameFormatter.Format(typeof(TCommand));

  /// <inheritdoc/>
  public string CommandType => TYPE_NAME;

  /// <inheritdoc/>
  public async ValueTask InvokeAsync(IServiceProvider services, string payload, Guid streamId, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var command = MultiStreamJson.Deserialize<TCommand>(payload)
      ?? throw new InvalidOperationException($"The stored payload of a {TYPE_NAME} run reads back as nothing; the stream cannot be worked.");
    var handler = services.GetRequiredService<IMultiStreamCommandHandler<TCommand>>();
    await handler.HandleStreamAsync(command, streamId, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc/>
  public void RegisterIntake(IReceptorRegistry registry, IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(registry);
    ArgumentNullException.ThrowIfNull(services);
    var intake = new MultiStreamCommandIntake<TCommand>(
      services.GetRequiredService<IMultiStreamCommandStore>(),
      services.GetService<ILogger<MultiStreamCommandIntake<TCommand>>>() ?? NullLogger<MultiStreamCommandIntake<TCommand>>.Instance);
    registry.Register(intake, LifecycleStage.LocalImmediateInline);
    registry.Register(intake, LifecycleStage.PreOutboxInline);
    registry.Register(intake, LifecycleStage.PostInboxInline);
  }
}
