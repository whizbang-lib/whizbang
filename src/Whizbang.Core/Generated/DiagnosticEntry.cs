namespace Whizbang.Core.Generated;

/// <summary>
/// Represents a diagnostic entry captured at build time.
/// </summary>
/// <param name="GeneratorName">Name of the source generator</param>
/// <param name="Timestamp">ISO 8601 timestamp when the diagnostic was captured</param>
/// <param name="Category">Category of the diagnostic</param>
/// <param name="Message">Diagnostic message</param>
public record DiagnosticEntry(
    string GeneratorName,
    string Timestamp,
    DiagnosticCategory Category,
    string Message
);
