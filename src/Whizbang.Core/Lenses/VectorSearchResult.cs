namespace Whizbang.Core.Lenses;

/// <summary>
/// Result of a vector similarity search containing the row, distance, and similarity score.
/// </summary>
/// <typeparam name="TModel">The perspective model type.</typeparam>
/// <param name="Row">The perspective row from the search.</param>
/// <param name="Distance">The distance from the search vector (lower is closer). Metric depends on the search type.</param>
/// <param name="Similarity">The similarity score (higher is more similar). For cosine, this is 1 - Distance.</param>
/// <docs>lenses/vector-search</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/VectorSearchExtensionsTests.cs</tests>
/// <remarks>
/// <para>
/// Distance values depend on the metric used:
/// <list type="bullet">
///   <item><description>Cosine: 0 (identical) to 2 (opposite)</description></item>
///   <item><description>L2 (Euclidean): 0 (identical) to unbounded</description></item>
///   <item><description>Inner Product: Depends on vector magnitudes (for normalized vectors: -1 to 1)</description></item>
/// </list>
/// </para>
/// <para>
/// Similarity is calculated as <c>1 - Distance</c> for cosine metric, giving a range of -1 (opposite) to 1 (identical).
/// For other metrics, similarity may need different interpretation.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var results = await lensQuery.Query
///     .WithCosineDistance("embedding", searchVector)
///     .OrderBy(r => r.Distance)
///     .Take(10)
///     .ToListAsync();
///
/// foreach (var result in results) {
///   Console.WriteLine($"{result.Row.Data.Name}: {result.Similarity:P0} similar");
/// }
/// </code>
/// </example>
public sealed record VectorSearchResult<TModel>(
    PerspectiveRow<TModel> Row,
    double Distance,
    double Similarity
) where TModel : class;
