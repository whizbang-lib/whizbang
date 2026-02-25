using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Whizbang.Core.Lenses;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Extension methods for vector similarity search on perspective queries.
/// Provides pgvector operator support (<![CDATA[<=>]]>, <![CDATA[<->]]>, <![CDATA[<#>]]>) via LINQ extensions.
/// </summary>
/// <remarks>
/// <para>
/// These extension methods are designed to work with PostgreSQL and the Pgvector.EntityFrameworkCore package.
/// When used with real PostgreSQL, the pgvector operators translate to efficient vector similarity queries.
/// </para>
/// <para>
/// <strong>Type-Safe API</strong>: All methods use strongly-typed lambda selectors for compile-time safety.
/// Use <c>m => m.Embedding</c> instead of string column names.
/// </para>
/// <para>
/// <strong>Requires</strong>: <c>Pgvector.EntityFrameworkCore</c> package when using <c>[VectorField]</c> attributes.
/// The WHIZ070 diagnostic will guide you to add this package if missing.
/// </para>
/// </remarks>
/// <docs>lenses/vector-search</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/VectorSearchIntegrationTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/VectorSearchExtensionsTests.cs</tests>
/// <example>
/// <code>
/// // Find similar documents ordered by cosine distance (constant vector)
/// var results = await lensQuery.Query
///     .WithinCosineDistance(m => m.Embedding, searchVector, threshold: 0.5)
///     .OrderByCosineDistance(m => m.Embedding, searchVector)
///     .Take(10)
///     .ToListAsync();
///
/// // Compare two columns (100% SQL, no round-trip)
/// var results = await lensQuery.Query
///     .OrderByCosineDistance(m => m.Embedding, m => m.ReferenceEmbedding)
///     .ToListAsync();
/// </code>
/// </example>
public static class VectorSearchExtensions {
  // ========================================
  // AOT-Safe MethodInfo Cache
  // Captured via expression lambda parsing at compile time
  // ========================================

  private static readonly System.Reflection.MethodInfo _cosineDistanceMethod =
      ((MethodCallExpression)((Expression<Func<Vector, Vector, double>>)
          ((v, search) => v.CosineDistance(search))).Body).Method;

  private static readonly System.Reflection.MethodInfo _l2DistanceMethod =
      ((MethodCallExpression)((Expression<Func<Vector, Vector, double>>)
          ((v, search) => v.L2Distance(search))).Body).Method;

  private static readonly System.Reflection.MethodInfo _maxInnerProductMethod =
      ((MethodCallExpression)((Expression<Func<Vector, Vector, double>>)
          ((v, search) => v.MaxInnerProduct(search))).Body).Method;

  // AOT-Safe: Capture EF.Property<Vector> MethodInfo at compile time
  // We use a dummy object and string to extract the method, then use MakeGenericMethod at call time
  private static readonly System.Reflection.MethodInfo _efPropertyVectorMethod =
      ((MethodCallExpression)((Expression<Func<object, Vector>>)
          (obj => EF.Property<Vector>(obj, ""))).Body).Method;

  // AOT-Safe: Capture EF.Property<object?> MethodInfo for null checks
  // Using object? avoids triggering Pgvector type handlers during IS NOT NULL checks
  private static readonly System.Reflection.MethodInfo _efPropertyObjectMethod =
      ((MethodCallExpression)((Expression<Func<object, object?>>)
          (obj => EF.Property<object?>(obj, ""))).Body).Method;

  // ========================================
  // OrderByCosineDistance - Constant Vector
  // ========================================

  /// <summary>
  /// Orders results by cosine distance to the search vector (closest first).
  /// PostgreSQL: <c>ORDER BY column <![CDATA[<=>]]> @search ASC</c>
  /// </summary>
  /// <typeparam name="TModel">The perspective model type.</typeparam>
  /// <param name="query">The queryable to order.</param>
  /// <param name="vectorSelector">Lambda expression selecting the vector property (e.g., m => m.Embedding).</param>
  /// <param name="searchVector">The vector to compare against.</param>
  /// <returns>An ordered queryable with results closest to search vector first.</returns>
  /// <exception cref="ArgumentNullException">Thrown when vectorSelector or searchVector is null.</exception>
  /// <exception cref="ArgumentException">Thrown when vectorSelector is not a valid property access expression.</exception>
  public static IOrderedQueryable<PerspectiveRow<TModel>> OrderByCosineDistance<TModel>(
      this IQueryable<PerspectiveRow<TModel>> query,
      Expression<Func<TModel, float[]?>> vectorSelector,
      float[] searchVector)
      where TModel : class {
    ArgumentNullException.ThrowIfNull(vectorSelector);
    ArgumentNullException.ThrowIfNull(searchVector);

    var propertyName = _getPropertyNameFromSelector(vectorSelector);
    var searchVectorValue = new Vector(searchVector);

    var param = Expression.Parameter(typeof(PerspectiveRow<TModel>), "r");
    // Vector columns are shadow properties on PerspectiveRow, not inside Data
    var vectorProperty = _buildEfPropertyAccess(param, propertyName);
    // Wrap search vector in closure pattern for proper SQL parameterization
    var searchVectorExpr = _buildParameterizedVectorExpression(searchVectorValue);
    // Extension methods are static - use Expression.Call(method, arg1, arg2)
    var distanceCall = Expression.Call(_cosineDistanceMethod, vectorProperty, searchVectorExpr);
    var lambda = Expression.Lambda<Func<PerspectiveRow<TModel>, double>>(distanceCall, param);

    return query.OrderBy(lambda);
  }

  /// <summary>
  /// Orders results by cosine distance between two vector columns (closest first).
  /// PostgreSQL: <c>ORDER BY column1 <![CDATA[<=>]]> column2 ASC</c>
  /// </summary>
  /// <remarks>
  /// This overload compares two columns in SQL without sending vector data to C#.
  /// Useful for finding rows where one embedding matches another embedding.
  /// </remarks>
  public static IOrderedQueryable<PerspectiveRow<TModel>> OrderByCosineDistance<TModel>(
      this IQueryable<PerspectiveRow<TModel>> query,
      Expression<Func<TModel, float[]?>> vectorSelector,
      Expression<Func<TModel, float[]?>> searchVectorSelector)
      where TModel : class {
    ArgumentNullException.ThrowIfNull(vectorSelector);
    ArgumentNullException.ThrowIfNull(searchVectorSelector);

    var vectorPropertyName = _getPropertyNameFromSelector(vectorSelector);
    var searchPropertyName = _getPropertyNameFromSelector(searchVectorSelector);

    var param = Expression.Parameter(typeof(PerspectiveRow<TModel>), "r");
    // Vector columns are shadow properties on PerspectiveRow, not inside Data
    var vectorProperty = _buildEfPropertyAccess(param, vectorPropertyName);
    var searchProperty = _buildEfPropertyAccess(param, searchPropertyName);
    // Extension methods are static - use Expression.Call(method, arg1, arg2)
    var distanceCall = Expression.Call(_cosineDistanceMethod, vectorProperty, searchProperty);
    var lambda = Expression.Lambda<Func<PerspectiveRow<TModel>, double>>(distanceCall, param);

    return query.OrderBy(lambda);
  }

  // ========================================
  // OrderByCosineDistance - Generic (Cross-Table)
  // ========================================

  /// <summary>
  /// Orders results by cosine distance between vectors from any queryable (including joins).
  /// PostgreSQL: <c>ORDER BY column1 <![CDATA[<=>]]> column2 ASC</c>
  /// </summary>
  /// <remarks>
  /// This generic overload works with any IQueryable&lt;T&gt;, enabling cross-table vector comparisons after joins.
  /// </remarks>
  public static IOrderedQueryable<T> OrderByCosineDistance<T>(
      this IQueryable<T> query,
      Expression<Func<T, float[]?>> vectorSelector,
      Expression<Func<T, float[]?>> searchVectorSelector)
      where T : class {
    ArgumentNullException.ThrowIfNull(vectorSelector);
    ArgumentNullException.ThrowIfNull(searchVectorSelector);

    var distanceExpression = _buildCrossTableDistanceExpression(
        vectorSelector, searchVectorSelector, _cosineDistanceMethod);

    return query.OrderBy(distanceExpression);
  }

  // ========================================
  // OrderByL2Distance - Constant Vector
  // ========================================

  /// <summary>
  /// Orders results by L2 (Euclidean) distance to the search vector (closest first).
  /// PostgreSQL: <c>ORDER BY column <![CDATA[<->]]> @search ASC</c>
  /// </summary>
  public static IOrderedQueryable<PerspectiveRow<TModel>> OrderByL2Distance<TModel>(
      this IQueryable<PerspectiveRow<TModel>> query,
      Expression<Func<TModel, float[]?>> vectorSelector,
      float[] searchVector)
      where TModel : class {
    ArgumentNullException.ThrowIfNull(vectorSelector);
    ArgumentNullException.ThrowIfNull(searchVector);

    var propertyName = _getPropertyNameFromSelector(vectorSelector);
    var searchVectorValue = new Vector(searchVector);

    var param = Expression.Parameter(typeof(PerspectiveRow<TModel>), "r");
    // Vector columns are shadow properties on PerspectiveRow, not inside Data
    var vectorProperty = _buildEfPropertyAccess(param, propertyName);
    // Wrap search vector in closure pattern for proper SQL parameterization
    var searchVectorExpr = _buildParameterizedVectorExpression(searchVectorValue);
    // Extension methods are static - use Expression.Call(method, arg1, arg2)
    var distanceCall = Expression.Call(_l2DistanceMethod, vectorProperty, searchVectorExpr);
    var lambda = Expression.Lambda<Func<PerspectiveRow<TModel>, double>>(distanceCall, param);

    return query.OrderBy(lambda);
  }

  /// <summary>
  /// Orders results by L2 distance between two vector columns.
  /// </summary>
  public static IOrderedQueryable<PerspectiveRow<TModel>> OrderByL2Distance<TModel>(
      this IQueryable<PerspectiveRow<TModel>> query,
      Expression<Func<TModel, float[]?>> vectorSelector,
      Expression<Func<TModel, float[]?>> searchVectorSelector)
      where TModel : class {
    ArgumentNullException.ThrowIfNull(vectorSelector);
    ArgumentNullException.ThrowIfNull(searchVectorSelector);

    var vectorPropertyName = _getPropertyNameFromSelector(vectorSelector);
    var searchPropertyName = _getPropertyNameFromSelector(searchVectorSelector);

    var param = Expression.Parameter(typeof(PerspectiveRow<TModel>), "r");
    // Vector columns are shadow properties on PerspectiveRow, not inside Data
    var vectorProperty = _buildEfPropertyAccess(param, vectorPropertyName);
    var searchProperty = _buildEfPropertyAccess(param, searchPropertyName);
    // Extension methods are static - use Expression.Call(method, arg1, arg2)
    var distanceCall = Expression.Call(_l2DistanceMethod, vectorProperty, searchProperty);
    var lambda = Expression.Lambda<Func<PerspectiveRow<TModel>, double>>(distanceCall, param);

    return query.OrderBy(lambda);
  }

  // ========================================
  // OrderByL2Distance - Generic (Cross-Table)
  // ========================================

  /// <summary>
  /// Orders results by L2 distance between vectors from any queryable (including joins).
  /// </summary>
  public static IOrderedQueryable<T> OrderByL2Distance<T>(
      this IQueryable<T> query,
      Expression<Func<T, float[]?>> vectorSelector,
      Expression<Func<T, float[]?>> searchVectorSelector)
      where T : class {
    ArgumentNullException.ThrowIfNull(vectorSelector);
    ArgumentNullException.ThrowIfNull(searchVectorSelector);

    var distanceExpression = _buildCrossTableDistanceExpression(
        vectorSelector, searchVectorSelector, _l2DistanceMethod);

    return query.OrderBy(distanceExpression);
  }

  // ========================================
  // OrderByInnerProductDistance - Constant Vector
  // ========================================

  /// <summary>
  /// Orders results by inner product distance to the search vector.
  /// PostgreSQL: <c>ORDER BY column <![CDATA[<#>]]> @search ASC</c>
  /// </summary>
  /// <remarks>
  /// Inner product is negated so that higher dot product = lower distance.
  /// Use with normalized vectors for best results.
  /// </remarks>
  public static IOrderedQueryable<PerspectiveRow<TModel>> OrderByInnerProductDistance<TModel>(
      this IQueryable<PerspectiveRow<TModel>> query,
      Expression<Func<TModel, float[]?>> vectorSelector,
      float[] searchVector)
      where TModel : class {
    ArgumentNullException.ThrowIfNull(vectorSelector);
    ArgumentNullException.ThrowIfNull(searchVector);

    var propertyName = _getPropertyNameFromSelector(vectorSelector);
    var searchVectorValue = new Vector(searchVector);

    var param = Expression.Parameter(typeof(PerspectiveRow<TModel>), "r");
    // Vector columns are shadow properties on PerspectiveRow, not inside Data
    var vectorProperty = _buildEfPropertyAccess(param, propertyName);
    // Wrap search vector in closure pattern for proper SQL parameterization
    var searchVectorExpr = _buildParameterizedVectorExpression(searchVectorValue);
    // Extension methods are static - use Expression.Call(method, arg1, arg2)
    var distanceCall = Expression.Call(_maxInnerProductMethod, vectorProperty, searchVectorExpr);
    var lambda = Expression.Lambda<Func<PerspectiveRow<TModel>, double>>(distanceCall, param);

    return query.OrderBy(lambda);
  }

  // ========================================
  // WithinCosineDistance - Constant Vector
  // ========================================

  /// <summary>
  /// Filters results to only include rows within the specified cosine distance threshold.
  /// PostgreSQL: <c>WHERE column <![CDATA[<=>]]> @search <![CDATA[<]]> @threshold</c>
  /// </summary>
  public static IQueryable<PerspectiveRow<TModel>> WithinCosineDistance<TModel>(
      this IQueryable<PerspectiveRow<TModel>> query,
      Expression<Func<TModel, float[]?>> vectorSelector,
      float[] searchVector,
      double threshold)
      where TModel : class {
    ArgumentNullException.ThrowIfNull(vectorSelector);
    ArgumentNullException.ThrowIfNull(searchVector);
    ArgumentOutOfRangeException.ThrowIfNegative(threshold);

    var propertyName = _getPropertyNameFromSelector(vectorSelector);
    var searchVectorValue = new Vector(searchVector);

    var param = Expression.Parameter(typeof(PerspectiveRow<TModel>), "r");
    // Vector columns are shadow properties on PerspectiveRow, not inside Data
    var vectorProperty = _buildEfPropertyAccess(param, propertyName);
    // Wrap search vector in closure pattern for proper SQL parameterization
    var searchVectorExpr = _buildParameterizedVectorExpression(searchVectorValue);
    // Extension methods are static - use Expression.Call(method, arg1, arg2)
    var distanceCall = Expression.Call(_cosineDistanceMethod, vectorProperty, searchVectorExpr);
    var comparison = Expression.LessThan(distanceCall, Expression.Constant(threshold));
    var lambda = Expression.Lambda<Func<PerspectiveRow<TModel>, bool>>(comparison, param);

    return query.Where(lambda);
  }

  /// <summary>
  /// Filters results to only include rows within the cosine distance threshold between two columns.
  /// </summary>
  public static IQueryable<PerspectiveRow<TModel>> WithinCosineDistance<TModel>(
      this IQueryable<PerspectiveRow<TModel>> query,
      Expression<Func<TModel, float[]?>> vectorSelector,
      Expression<Func<TModel, float[]?>> searchVectorSelector,
      double threshold)
      where TModel : class {
    ArgumentNullException.ThrowIfNull(vectorSelector);
    ArgumentNullException.ThrowIfNull(searchVectorSelector);
    ArgumentOutOfRangeException.ThrowIfNegative(threshold);

    var vectorPropertyName = _getPropertyNameFromSelector(vectorSelector);
    var searchPropertyName = _getPropertyNameFromSelector(searchVectorSelector);

    var param = Expression.Parameter(typeof(PerspectiveRow<TModel>), "r");
    // Vector columns are shadow properties on PerspectiveRow, not inside Data
    var vectorProperty = _buildEfPropertyAccess(param, vectorPropertyName);
    var searchProperty = _buildEfPropertyAccess(param, searchPropertyName);
    // Extension methods are static - use Expression.Call(method, arg1, arg2)
    var distanceCall = Expression.Call(_cosineDistanceMethod, vectorProperty, searchProperty);
    var comparison = Expression.LessThan(distanceCall, Expression.Constant(threshold));
    var lambda = Expression.Lambda<Func<PerspectiveRow<TModel>, bool>>(comparison, param);

    return query.Where(lambda);
  }

  // ========================================
  // WithinCosineDistance - Generic (Cross-Table)
  // ========================================

  /// <summary>
  /// Filters results to only include rows within cosine distance threshold (cross-table).
  /// </summary>
  public static IQueryable<T> WithinCosineDistance<T>(
      this IQueryable<T> query,
      Expression<Func<T, float[]?>> vectorSelector,
      Expression<Func<T, float[]?>> searchVectorSelector,
      double threshold)
      where T : class {
    ArgumentNullException.ThrowIfNull(vectorSelector);
    ArgumentNullException.ThrowIfNull(searchVectorSelector);
    ArgumentOutOfRangeException.ThrowIfNegative(threshold);

    var filterExpression = _buildCrossTableFilterExpression(
        vectorSelector, searchVectorSelector, _cosineDistanceMethod, threshold);

    return query.Where(filterExpression);
  }

  // ========================================
  // WithinL2Distance - Constant Vector
  // ========================================

  /// <summary>
  /// Filters results to only include rows within the specified L2 (Euclidean) distance threshold.
  /// PostgreSQL: <c>WHERE column <![CDATA[<->]]> @search <![CDATA[<]]> @threshold</c>
  /// </summary>
  public static IQueryable<PerspectiveRow<TModel>> WithinL2Distance<TModel>(
      this IQueryable<PerspectiveRow<TModel>> query,
      Expression<Func<TModel, float[]?>> vectorSelector,
      float[] searchVector,
      double threshold)
      where TModel : class {
    ArgumentNullException.ThrowIfNull(vectorSelector);
    ArgumentNullException.ThrowIfNull(searchVector);
    ArgumentOutOfRangeException.ThrowIfNegative(threshold);

    var propertyName = _getPropertyNameFromSelector(vectorSelector);
    var searchVectorValue = new Vector(searchVector);

    var param = Expression.Parameter(typeof(PerspectiveRow<TModel>), "r");
    // Vector columns are shadow properties on PerspectiveRow, not inside Data
    var vectorProperty = _buildEfPropertyAccess(param, propertyName);
    // Wrap search vector in closure pattern for proper SQL parameterization
    var searchVectorExpr = _buildParameterizedVectorExpression(searchVectorValue);
    // Extension methods are static - use Expression.Call(method, arg1, arg2)
    var distanceCall = Expression.Call(_l2DistanceMethod, vectorProperty, searchVectorExpr);
    var comparison = Expression.LessThan(distanceCall, Expression.Constant(threshold));
    var lambda = Expression.Lambda<Func<PerspectiveRow<TModel>, bool>>(comparison, param);

    return query.Where(lambda);
  }

  /// <summary>
  /// Filters results to only include rows within L2 distance threshold between two columns.
  /// </summary>
  public static IQueryable<PerspectiveRow<TModel>> WithinL2Distance<TModel>(
      this IQueryable<PerspectiveRow<TModel>> query,
      Expression<Func<TModel, float[]?>> vectorSelector,
      Expression<Func<TModel, float[]?>> searchVectorSelector,
      double threshold)
      where TModel : class {
    ArgumentNullException.ThrowIfNull(vectorSelector);
    ArgumentNullException.ThrowIfNull(searchVectorSelector);
    ArgumentOutOfRangeException.ThrowIfNegative(threshold);

    var vectorPropertyName = _getPropertyNameFromSelector(vectorSelector);
    var searchPropertyName = _getPropertyNameFromSelector(searchVectorSelector);

    var param = Expression.Parameter(typeof(PerspectiveRow<TModel>), "r");
    // Vector columns are shadow properties on PerspectiveRow, not inside Data
    var vectorProperty = _buildEfPropertyAccess(param, vectorPropertyName);
    var searchProperty = _buildEfPropertyAccess(param, searchPropertyName);
    // Extension methods are static - use Expression.Call(method, arg1, arg2)
    var distanceCall = Expression.Call(_l2DistanceMethod, vectorProperty, searchProperty);
    var comparison = Expression.LessThan(distanceCall, Expression.Constant(threshold));
    var lambda = Expression.Lambda<Func<PerspectiveRow<TModel>, bool>>(comparison, param);

    return query.Where(lambda);
  }

  // ========================================
  // WhereHasVector - Filter NULL vectors
  // ========================================

  /// <summary>
  /// Filters out rows where the vector column is NULL.
  /// PostgreSQL: <c>WHERE column IS NOT NULL</c>
  /// </summary>
  /// <remarks>
  /// Use this before vector distance operations to avoid "Nullable object must have a value" errors.
  /// Rows with NULL vectors cannot participate in distance calculations.
  /// </remarks>
  /// <typeparam name="TModel">The perspective model type.</typeparam>
  /// <param name="query">The queryable to filter.</param>
  /// <param name="vectorSelector">Lambda expression selecting the vector property (e.g., m => m.Embedding).</param>
  /// <returns>A filtered queryable containing only rows with non-null vectors.</returns>
  public static IQueryable<PerspectiveRow<TModel>> WhereHasVector<TModel>(
      this IQueryable<PerspectiveRow<TModel>> query,
      Expression<Func<TModel, float[]?>> vectorSelector)
      where TModel : class {
    ArgumentNullException.ThrowIfNull(vectorSelector);

    var propertyName = _getPropertyNameFromSelector(vectorSelector);

    var param = Expression.Parameter(typeof(PerspectiveRow<TModel>), "r");
    // Use EF.Property<object?> for null check to avoid triggering Pgvector type handlers
    // EF Core translates this to: WHERE shadow_column IS NOT NULL
    var vectorProperty = _buildEfPropertyAccessForNullCheck(param, propertyName);
    var nullCheck = Expression.NotEqual(vectorProperty, Expression.Constant(null, typeof(object)));
    var lambda = Expression.Lambda<Func<PerspectiveRow<TModel>, bool>>(nullCheck, param);

    return query.Where(lambda);
  }

  // ========================================
  // WithCosineDistance - Project with Distance/Similarity
  // ========================================

  /// <summary>
  /// Projects rows with cosine distance and similarity scores.
  /// PostgreSQL: <c>SELECT *, (column <![CDATA[<=>]]> @search) AS Distance</c>
  /// </summary>
  /// <remarks>
  /// Returns <see cref="VectorSearchResult{TModel}"/> with Distance (0 = identical, 2 = opposite)
  /// and Similarity (1 = identical, -1 = opposite).
  /// </remarks>
  public static IQueryable<VectorSearchResult<TModel>> WithCosineDistance<TModel>(
      this IQueryable<PerspectiveRow<TModel>> query,
      Expression<Func<TModel, float[]?>> vectorSelector,
      float[] searchVector)
      where TModel : class {
    ArgumentNullException.ThrowIfNull(vectorSelector);
    ArgumentNullException.ThrowIfNull(searchVector);

    var propertyName = _getPropertyNameFromSelector(vectorSelector);
    var searchVectorValue = new Vector(searchVector);

    var param = Expression.Parameter(typeof(PerspectiveRow<TModel>), "r");
    // Vector columns are shadow properties on PerspectiveRow, not inside Data
    var vectorProperty = _buildEfPropertyAccess(param, propertyName);
    // Wrap search vector in closure pattern for proper SQL parameterization
    var searchVectorExpr = _buildParameterizedVectorExpression(searchVectorValue);
    // Extension methods are static - use Expression.Call(method, arg1, arg2)
    var distanceCall = Expression.Call(_cosineDistanceMethod, vectorProperty, searchVectorExpr);

    // Similarity = 1 - distance
    var oneConstant = Expression.Constant(1.0);
    var similarityExpr = Expression.Subtract(oneConstant, distanceCall);

    // AOT-safe: Extract constructor info from compile-time lambda expression
    var resultCtor = _getVectorSearchResultConstructor<TModel>();

    var newExpr = Expression.New(resultCtor, param, distanceCall, similarityExpr);
    var lambda = Expression.Lambda<Func<PerspectiveRow<TModel>, VectorSearchResult<TModel>>>(newExpr, param);

    return query.Select(lambda);
  }

  // ========================================
  // Static Distance Calculators
  // These are utility methods for manual distance calculations
  // Used by integration tests or manual operations
  // ========================================

  /// <summary>
  /// Calculates cosine distance between two vectors.
  /// Cosine distance = 1 - cosine_similarity
  /// Range: 0 (identical) to 2 (opposite)
  /// </summary>
  public static double CalculateCosineDistance(float[] a, float[] b) {
    ArgumentNullException.ThrowIfNull(a);
    ArgumentNullException.ThrowIfNull(b);

    if (a.Length == 0 || b.Length == 0 || a.Length != b.Length) {
      return double.MaxValue;
    }

    double dotProduct = 0;
    double magnitudeA = 0;
    double magnitudeB = 0;

    for (int i = 0; i < a.Length; i++) {
      dotProduct += a[i] * b[i];
      magnitudeA += a[i] * a[i];
      magnitudeB += b[i] * b[i];
    }

    magnitudeA = Math.Sqrt(magnitudeA);
    magnitudeB = Math.Sqrt(magnitudeB);

    if (magnitudeA == 0 || magnitudeB == 0) {
      return double.MaxValue;
    }

    var cosineSimilarity = dotProduct / (magnitudeA * magnitudeB);
    return 1.0 - cosineSimilarity;
  }

  /// <summary>
  /// Calculates L2 (Euclidean) distance between two vectors.
  /// Range: 0 (identical) to unbounded
  /// </summary>
  public static double CalculateL2Distance(float[] a, float[] b) {
    ArgumentNullException.ThrowIfNull(a);
    ArgumentNullException.ThrowIfNull(b);

    if (a.Length == 0 || b.Length == 0 || a.Length != b.Length) {
      return double.MaxValue;
    }

    double sumSquaredDiff = 0;
    for (int i = 0; i < a.Length; i++) {
      var diff = a[i] - b[i];
      sumSquaredDiff += diff * diff;
    }

    return Math.Sqrt(sumSquaredDiff);
  }

  /// <summary>
  /// Calculates inner product distance between two vectors.
  /// Inner product distance = -dot_product (negated so smaller = more similar)
  /// </summary>
  public static double CalculateInnerProductDistance(float[] a, float[] b) {
    ArgumentNullException.ThrowIfNull(a);
    ArgumentNullException.ThrowIfNull(b);

    if (a.Length == 0 || b.Length == 0 || a.Length != b.Length) {
      return double.MaxValue;
    }

    double dotProduct = 0;
    for (int i = 0; i < a.Length; i++) {
      dotProduct += a[i] * b[i];
    }

    // Negate so that higher dot product = lower distance
    return -dotProduct;
  }

  // ========================================
  // Private Helpers (AOT-Safe)
  // ========================================

  /// <summary>
  /// Extracts property name from a lambda expression selector.
  /// AOT-safe: Uses pattern matching on expression types.
  /// </summary>
  private static string _getPropertyNameFromSelector<TModel>(Expression<Func<TModel, float[]?>> selector) {
    return selector.Body switch {
      MemberExpression member => member.Member.Name,
      UnaryExpression { Operand: MemberExpression inner } => inner.Member.Name,
      _ => throw new ArgumentException(
          $"Invalid vector selector. Expected property access like 'm => m.Embedding', got: {selector}")
    };
  }

  /// <summary>
  /// Builds EF.Property&lt;Vector&gt; access expression for shadow property.
  /// AOT-safe: Uses cached MethodInfo from compile-time expression parsing.
  /// </summary>
  /// <remarks>
  /// IMPORTANT: Shadow properties are named using snake_case to match the EF Core generator convention.
  /// E.g., property "Embeddings" → shadow property "embeddings", "ContentEmbedding" → "content_embedding".
  /// </remarks>
  private static MethodCallExpression _buildEfPropertyAccess(Expression instance, string propertyName) {
    // Convert PascalCase property name to snake_case to match EF Core shadow property naming
    var shadowPropertyName = _toSnakeCase(propertyName);
    // Build: EF.Property<Vector>(instance, shadowPropertyName)
    // Using cached _efPropertyVectorMethod instead of string-based Expression.Call
    return Expression.Call(_efPropertyVectorMethod, instance, Expression.Constant(shadowPropertyName));
  }

  /// <summary>
  /// Builds EF.Property&lt;object?&gt; access expression for null checking shadow properties.
  /// Uses object? type to avoid triggering Pgvector type handlers during null checks.
  /// </summary>
  private static MethodCallExpression _buildEfPropertyAccessForNullCheck(Expression instance, string propertyName) {
    var shadowPropertyName = _toSnakeCase(propertyName);
    // Build: EF.Property<object?>(instance, shadowPropertyName)
    // Using object? avoids Pgvector type resolution issues when checking IS NOT NULL
    return Expression.Call(_efPropertyObjectMethod, instance, Expression.Constant(shadowPropertyName));
  }

  /// <summary>
  /// Converts PascalCase to snake_case.
  /// E.g., "Embeddings" → "embeddings", "ContentEmbedding" → "content_embedding".
  /// </summary>
  /// <remarks>
  /// This matches the naming convention used by EFCorePerspectiveConfigurationGenerator
  /// for shadow properties. Must stay in sync with NamingConventionUtilities.ToSnakeCase().
  /// </remarks>
  private static string _toSnakeCase(string input) {
    if (string.IsNullOrEmpty(input)) {
      return input;
    }

    var sb = new System.Text.StringBuilder();
    sb.Append(char.ToLowerInvariant(input[0]));

    for (int i = 1; i < input.Length; i++) {
      char c = input[i];
      if (char.IsUpper(c)) {
        sb.Append('_');
        sb.Append(char.ToLowerInvariant(c));
      } else {
        sb.Append(c);
      }
    }

    return sb.ToString();
  }

  /// <summary>
  /// Builds a parameterized expression for a Vector value.
  /// Wraps the value in a closure pattern so EF Core parameterizes it correctly.
  /// </summary>
  /// <remarks>
  /// Using Expression.Constant(vector) directly causes EF Core to try to embed the value as a SQL literal,
  /// which fails for Vector types. By wrapping in an anonymous object and using MemberAccess,
  /// EF Core treats this as a captured closure variable and parameterizes it properly.
  /// </remarks>
  private static MemberExpression _buildParameterizedVectorExpression(Vector searchVector) {
    // Create holder object: new VectorHolder { Value = searchVector }
    // This simulates a closure capture, which EF Core parameterizes correctly
    var holder = new VectorHolder { Value = searchVector };
    var holderExpr = Expression.Constant(holder);
    // AOT-safe: Use MakeMemberAccess with compile-time PropertyInfo from VectorHolderValueProperty
    return Expression.MakeMemberAccess(holderExpr, _vectorHolderValueProperty);
  }

  // AOT-safe: Extract PropertyInfo from compile-time expression
  private static readonly System.Reflection.PropertyInfo _vectorHolderValueProperty =
      ((MemberExpression)((Expression<Func<VectorHolder, Vector>>)(h => h.Value)).Body).Member as System.Reflection.PropertyInfo
      ?? throw new InvalidOperationException("Failed to extract VectorHolder.Value property");

  /// <summary>
  /// Helper class to hold Vector values for parameterization.
  /// EF Core parameterizes member access on constant objects.
  /// </summary>
  private sealed class VectorHolder {
    public Vector Value { get; set; } = null!;
  }

  /// <summary>
  /// Builds a MemberExpression accessing the Data property of PerspectiveRow&lt;TModel&gt;.
  /// AOT-safe: Extracts PropertyInfo from a compile-time lambda expression.
  /// </summary>
  private static MemberExpression _buildDataPropertyAccess<TModel>(ParameterExpression param) where TModel : class {
    // Build: r => r.Data - extract MemberInfo at compile time from lambda
    Expression<Func<PerspectiveRow<TModel>, TModel>> dataSelector = r => r.Data;
    var memberExpr = (MemberExpression)dataSelector.Body;
    // Rebind to our parameter using the extracted MemberInfo
    return Expression.MakeMemberAccess(param, memberExpr.Member);
  }

  /// <summary>
  /// Gets the constructor for VectorSearchResult&lt;TModel&gt;.
  /// AOT-safe: Extracts ConstructorInfo from a compile-time new expression.
  /// </summary>
  private static System.Reflection.ConstructorInfo _getVectorSearchResultConstructor<TModel>() where TModel : class {
    // Extract constructor from compile-time new expression
    Expression<Func<VectorSearchResult<TModel>>> ctorExpr =
        () => new VectorSearchResult<TModel>(default!, default, default);
    var newExpr = (NewExpression)ctorExpr.Body;
    return newExpr.Constructor!;
  }

  /// <summary>
  /// Extracts property path for vector access from a cross-table expression.
  /// Handles the pattern x => x.Row.Data.VectorProperty by returning the PerspectiveRow (x.Row)
  /// since vector properties are shadow properties on PerspectiveRow, not inside Data.
  /// </summary>
  private static (Expression path, string propertyName) _extractPropertyPath<T>(
      Expression<Func<T, float[]?>> selector,
      ParameterExpression param) {
    Expression? current = selector.Body;

    // Handle nullable conversions
    if (current is UnaryExpression unary) {
      current = unary.Operand;
    }

    var members = new List<System.Reflection.MemberInfo>();
    while (current is MemberExpression member) {
      members.Insert(0, member.Member);
      current = member.Expression;
    }

    if (members.Count == 0) {
      throw new ArgumentException($"Invalid selector: expected property path, got {selector}");
    }

    // Build the expression path from parameter
    // Special case: if path ends with .Data.VectorProperty, skip .Data
    // because vector columns are shadow properties on PerspectiveRow, not inside Data
    Expression result = param;
    int stopIndex = members.Count - 1; // Default: navigate all but last

    // Check if second-to-last member is "Data" - if so, skip it
    if (members.Count >= 2 && members[^2].Name == "Data") {
      stopIndex = members.Count - 2; // Skip both Data and the vector property
    }

    for (int i = 0; i < stopIndex; i++) {
      result = Expression.MakeMemberAccess(result, members[i]);
    }

    var lastPropertyName = members[^1].Name;
    return (result, lastPropertyName);
  }

  /// <summary>
  /// Builds a cross-table distance expression for ordering.
  /// </summary>
  private static Expression<Func<T, double>> _buildCrossTableDistanceExpression<T>(
      Expression<Func<T, float[]?>> vectorSelector,
      Expression<Func<T, float[]?>> searchVectorSelector,
      System.Reflection.MethodInfo distanceMethod)
      where T : class {
    var param = Expression.Parameter(typeof(T), "x");

    var (vectorPath, vectorPropName) = _extractPropertyPath(vectorSelector, param);
    var (searchPath, searchPropName) = _extractPropertyPath(searchVectorSelector, param);

    var vectorAccess = _buildEfPropertyAccess(vectorPath, vectorPropName);
    var searchAccess = _buildEfPropertyAccess(searchPath, searchPropName);

    // Extension methods are static - use Expression.Call(method, arg1, arg2)
    var distanceCall = Expression.Call(distanceMethod, vectorAccess, searchAccess);
    return Expression.Lambda<Func<T, double>>(distanceCall, param);
  }

  /// <summary>
  /// Builds a cross-table filter expression.
  /// </summary>
  private static Expression<Func<T, bool>> _buildCrossTableFilterExpression<T>(
      Expression<Func<T, float[]?>> vectorSelector,
      Expression<Func<T, float[]?>> searchVectorSelector,
      System.Reflection.MethodInfo distanceMethod,
      double threshold)
      where T : class {
    var param = Expression.Parameter(typeof(T), "x");

    var (vectorPath, vectorPropName) = _extractPropertyPath(vectorSelector, param);
    var (searchPath, searchPropName) = _extractPropertyPath(searchVectorSelector, param);

    var vectorAccess = _buildEfPropertyAccess(vectorPath, vectorPropName);
    var searchAccess = _buildEfPropertyAccess(searchPath, searchPropName);

    // Extension methods are static - use Expression.Call(method, arg1, arg2)
    var distanceCall = Expression.Call(distanceMethod, vectorAccess, searchAccess);
    var comparison = Expression.LessThan(distanceCall, Expression.Constant(threshold));

    return Expression.Lambda<Func<T, bool>>(comparison, param);
  }
}
