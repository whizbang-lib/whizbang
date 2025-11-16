using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Policies;

namespace Whizbang.Core.Data;

/// <summary>
/// Validates JSONB column sizes in C# before persistence.
/// Calculates byte sizes, logs warnings, and adds size metadata when thresholds crossed.
/// Size is NOT stored in metadata unless threshold is violated (for troubleshooting).
/// </summary>
public class JsonbSizeValidator {
  /// <summary>
  /// TOAST compression threshold: PostgreSQL begins compressing columns > 2KB.
  /// Performance impact: ~2× slower than uncompressed.
  /// </summary>
  private const int TOAST_COMPRESSION_THRESHOLD = 2_000;

  /// <summary>
  /// TOAST externalization threshold: Compressed data > 2KB moved to TOAST table.
  /// Raw data ~7KB compresses to ~2KB (3-4× compression ratio).
  /// Performance impact: ~5-10× slower than inline storage.
  /// </summary>
  private const int TOAST_EXTERNALIZATION_THRESHOLD = 7_000;

  private readonly ILogger<JsonbSizeValidator> _logger;
  private readonly JsonSerializerContext _jsonContext;

  public JsonbSizeValidator(ILogger<JsonbSizeValidator> logger, JsonSerializerContext jsonContext) {
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _jsonContext = jsonContext ?? throw new ArgumentNullException(nameof(jsonContext));
  }

  /// <summary>
  /// Validates JSONB size and adds warning to metadata if threshold crossed.
  /// Returns updated model with size warning in metadata (if applicable).
  /// </summary>
  /// <param name="model">The JSONB persistence model to validate</param>
  /// <param name="typeName">Name of the type being persisted (for logging)</param>
  /// <param name="policy">Optional policy configuration for size limits</param>
  /// <returns>Updated model (potentially with size warning added to metadata)</returns>
  /// <exception cref="InvalidOperationException">If size exceeds threshold and policy.ThrowOnSizeExceeded is true</exception>
  public JsonbPersistenceModel Validate(
    JsonbPersistenceModel model,
    string typeName,
    PolicyConfiguration? policy
  ) {
    // Skip validation if policy suppresses warnings
    if (policy?.SuppressSizeWarnings == true) {
      return model;
    }

    // Get threshold from policy or use default
    var threshold = policy?.MaxDataSizeBytes ?? TOAST_EXTERNALIZATION_THRESHOLD;

    // Calculate size in C# (NOT stored unless threshold crossed)
    var dataSize = model.DataSizeBytes;

    // Check TOAST externalization threshold (7KB default)
    if (dataSize >= threshold) {
      var message = $"{typeName} data size ({dataSize:N0} bytes) exceeds TOAST externalization threshold ({threshold:N0} bytes). " +
                    $"This will cause 5-10× performance degradation. " +
                    $"Consider splitting data or using fixed columns for frequently-queried fields.";

      _logger.LogWarning(message);

      // Add warning to metadata (only when threshold crossed)
      model = AddSizeWarningToMetadata(model, dataSize, threshold, "externalized");

      // Optionally throw exception
      if (policy?.ThrowOnSizeExceeded == true) {
        throw new InvalidOperationException(message);
      }
    }
    // Check TOAST compression threshold (2KB)
    else if (dataSize >= TOAST_COMPRESSION_THRESHOLD) {
      var message = $"{typeName} data size ({dataSize:N0} bytes) exceeds TOAST compression threshold (2,000 bytes). " +
                    $"This will cause ~2× performance degradation due to compression overhead.";

      _logger.LogInformation(message);

      // Add warning to metadata (only when threshold crossed)
      model = AddSizeWarningToMetadata(model, dataSize, TOAST_COMPRESSION_THRESHOLD, "compressed");
    }

    return model;
  }

  /// <summary>
  /// Adds size warning to metadata JSON.
  /// Only called when threshold is crossed (for troubleshooting).
  /// AOT-compatible using JsonDocument for reading and Utf8JsonWriter for writing.
  /// </summary>
  private JsonbPersistenceModel AddSizeWarningToMetadata(
    JsonbPersistenceModel model,
    int actualSize,
    int threshold,
    string warningType
  ) {
    try {
      using var stream = new MemoryStream();
      using var writer = new Utf8JsonWriter(stream);

      writer.WriteStartObject();

      // First, copy existing metadata fields if present
      if (!string.IsNullOrEmpty(model.MetadataJson)) {
        using var doc = JsonDocument.Parse(model.MetadataJson);
        foreach (var property in doc.RootElement.EnumerateObject()) {
          property.WriteTo(writer);
        }
      }

      // Add size warning fields (only stored when threshold crossed)
      writer.WriteString("__size_warning", warningType);
      writer.WriteNumber("__size_bytes", actualSize);
      writer.WriteNumber("__size_threshold", threshold);

      writer.WriteEndObject();
      writer.Flush();

      var updatedMetadataJson = System.Text.Encoding.UTF8.GetString(stream.ToArray());
      return model with { MetadataJson = updatedMetadataJson };
    } catch (JsonException ex) {
      // If metadata JSON is malformed, log error but don't fail validation
      _logger.LogError(ex, "Failed to add size warning to metadata JSON. Proceeding without size metadata.");
      return model;
    }
  }
}
