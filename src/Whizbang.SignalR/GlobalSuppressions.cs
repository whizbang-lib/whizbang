using System.Diagnostics.CodeAnalysis;

// S3604: False positive on primary constructor field captures (e.g., `private readonly ILogger _logger = logger;`)
// These are NOT redundant — they're the only way to capture primary constructor params into fields.
[assembly: SuppressMessage("csharpsquid", "S3604", Justification = "Primary constructor field capture pattern")]

// S3928: False positive on primary constructor parameter names in ArgumentException
// SonarQube doesn't recognize primary constructor parameters as valid parameter names.
[assembly: SuppressMessage("csharpsquid", "S3928", Justification = "Primary constructor parameter names are valid")]
