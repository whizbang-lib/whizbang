using HotChocolate;
using HotChocolate.Execution.Configuration;
using HotChocolate.Resolvers;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Lineage;

namespace Whizbang.Transports.HotChocolate;

/// <summary>
/// The <c>whizbangApplyStacks</c> and <c>whizbangApplyStackStreams</c> query fields: the same
/// <see cref="ApplyStackReport"/> / <see cref="ApplyStackStreamsReport"/> every surface serves,
/// contributed as a type extension so <c>[Authorize]</c> and the existing Whizbang GraphQL
/// security integration apply to them exactly as to any other field.
/// </summary>
/// <remarks>
/// The projection lives in <see cref="ApplyStackReporter"/>, shared with the minimal-API and
/// FastEndpoints surfaces — the transports cannot drift apart in what they disclose: event-type
/// topology and counts, never payloads.
/// </remarks>
/// <docs>proposals/pre-destruction-seam#serving-the-view</docs>
/// <tests>tests/Whizbang.Transports.HotChocolate.Tests/Unit/ApplyStackQueryTests.cs</tests>
[ExtendObjectType(OperationTypeNames.Query)]
public sealed class WhizbangApplyStackQueries {
#pragma warning disable CA1822 // HotChocolate binds resolvers on instance members of the type extension
  /// <summary>The path signatures matching the filters, plus the anchored flow view when an anchor is given.</summary>
  public Task<ApplyStackReport> GetWhizbangApplyStacksAsync(
      IResolverContext context,
      string? perspective = null,
      string? scope = null,
      int max = 200,
      string? anchor = null,
      int radius = 3,
      int branches = 10,
      CancellationToken cancellationToken = default) {
    return ApplyStackReporter.BuildAsync(
      context.Services.GetService<IApplyStackQuery>(),
      new ApplyStackQueryOptions { PerspectiveName = perspective, ScopeJson = scope, MaxSignatures = max },
      anchor,
      radius,
      branches,
      cancellationToken);
  }

  /// <summary>The stream ids whose collapsed path equals <paramref name="steps"/> exactly — the drill-in.</summary>
  public Task<ApplyStackStreamsReport> GetWhizbangApplyStackStreamsAsync(
      IResolverContext context,
      string[] steps,
      string? perspective = null,
      string? scope = null,
      int limit = 100,
      CancellationToken cancellationToken = default) {
    return ApplyStackReporter.BuildStreamsAsync(
      context.Services.GetService<IApplyStackQuery>(),
      steps,
      new ApplyStackQueryOptions { PerspectiveName = perspective, ScopeJson = scope },
      limit,
      cancellationToken);
  }
#pragma warning restore CA1822
}

/// <summary>Registers the apply-stack query fields. Opt-in — one explicit call.</summary>
/// <docs>proposals/pre-destruction-seam#serving-the-view</docs>
public static class HotChocolateApplyStackExtensions {
  /// <summary>
  /// Adds the <c>whizbangApplyStacks</c> and <c>whizbangApplyStackStreams</c> query fields to the
  /// schema. Publishing event-type topology is the host's decision, so nothing contributes these
  /// fields implicitly.
  /// </summary>
  /// <param name="builder">The HotChocolate request executor builder.</param>
  public static IRequestExecutorBuilder AddWhizbangApplyStacks(this IRequestExecutorBuilder builder) {
    ArgumentNullException.ThrowIfNull(builder);
    return builder.AddTypeExtension<WhizbangApplyStackQueries>();
  }
}
