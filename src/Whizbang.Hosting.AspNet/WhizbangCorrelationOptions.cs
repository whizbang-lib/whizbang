namespace Whizbang.Hosting.AspNet;

/// <summary>
/// Options for <see cref="WhizbangCorrelationMiddleware"/> — the request headers to read an inbound
/// correlation id from, in priority order (the first Guid-parseable value wins). Defaults to
/// <c>X-Correlation-ID</c>. Configure with <c>services.Configure&lt;WhizbangCorrelationOptions&gt;(...)</c>.
/// </summary>
public sealed class WhizbangCorrelationOptions {
  /// <summary>
  /// The request header names to read, in priority order. Defaults to a single <c>X-Correlation-ID</c> entry.
  /// </summary>
  public IList<string> HeaderNames { get; } = new List<string> { "X-Correlation-ID" };
}
