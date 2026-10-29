// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Sagas;

/// <summary>
/// The <see cref="ISagaItemStreamIdDeriver"/> <c>AddWhizbangSagas</c> registers: derives under the
/// <see cref="SagaOptions.PerItemStreamNamespace"/> of the options it was given.
/// </summary>
/// <docs>fundamentals/sagas/whizbang-sagas#per-item-streams-cross-pod-safe-fan-out</docs>
/// <tests>tests/Whizbang.Sagas.Tests/SagaItemStreamIdDeriverTests.cs</tests>
public sealed class SagaItemStreamIdDeriver : ISagaItemStreamIdDeriver {
  private readonly SagaOptions? _options;

  /// <summary>Creates a deriver for the namespace <paramref name="options"/> configures.</summary>
  /// <param name="options">The host's saga options.</param>
  /// <tests>tests/Whizbang.Sagas.Tests/SagaItemStreamIdDeriverTests.cs:Constructor_WithoutOptions_ThrowsAsync</tests>
  public SagaItemStreamIdDeriver(SagaOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    _options = options;
  }

  private SagaItemStreamIdDeriver() {
  }

  /// <summary>
  /// The process-wide deriver: reads <see cref="SagaItemStreams.AppDefaultNamespace"/> at call time, as
  /// <see cref="SagaItemStreams.Of(Guid, string)"/> does.
  /// </summary>
  /// <tests>tests/Whizbang.Sagas.Tests/SagaItemStreamIdDeriverTests.cs:ProcessWide_DerivesUnderTheNamespaceItReportsAsync</tests>
  internal static SagaItemStreamIdDeriver ProcessWide { get; } = new();

  /// <inheritdoc />
  public Guid NamespaceId => _options?.PerItemStreamNamespace ?? SagaItemStreams.AppDefaultNamespace;

  /// <inheritdoc />
  public Guid StreamIdOf(Guid sagaId, string itemIdentifier) => SagaItemStreams.Of(NamespaceId, sagaId, itemIdentifier);

  /// <summary>
  /// The deriver a saga service built with <paramref name="options"/> uses: one for those options when a container's
  /// <c>AddWhizbangSagas</c> registered them, so the service derives under its own host's namespace; otherwise
  /// <see cref="ProcessWide"/>.
  /// </summary>
  /// <remarks>
  /// Options a service built itself carry the framework default namespace unless set, and reading them as the host's
  /// would move a service whose host configured a namespace onto streams it never wrote. Those, and no options at all,
  /// keep the process-wide namespace the framework always used.
  /// </remarks>
  /// <param name="options">The options the saga service was given, if any.</param>
  /// <returns>The deriver for the service's per-item stream ids.</returns>
  /// <tests>tests/Whizbang.Sagas.Tests/SagaItemStreamIdDeriverTests.cs:For_OptionsAContainerRegistered_DeriveUnderThoseOptionsAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/SagaItemStreamIdDeriverTests.cs:For_OptionsNotRegisteredByAContainer_OrNone_UseTheProcessWideNamespaceAsync</tests>
  internal static ISagaItemStreamIdDeriver For(SagaOptions? options) =>
    options is { RegisteredWithHost: true } ? new SagaItemStreamIdDeriver(options) : ProcessWide;
}
