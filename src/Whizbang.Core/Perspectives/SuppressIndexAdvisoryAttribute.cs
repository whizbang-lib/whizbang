namespace Whizbang.Core.Perspectives;

/// <summary>
/// Declares that a perspective field is deliberately queried without a physical index, silencing
/// both the WHIZ302 build warning and the runtime index advisory raised by the maintenance cycle.
/// The reason is required: an opt-out has to read as a decision in review, not as noise.
/// </summary>
/// <remarks>
/// <para>
/// Filtering a perspective on a property that lives only in the model's JSON payload makes the
/// database read every row of the table. That is correct but linear, so the framework says so at
/// build time and again from the maintenance cycle once it can see the cost on a live table.
/// Sometimes linear is the right answer: a perspective that holds a handful of rows by
/// construction, a filter that runs once a day, a column whose selectivity would make an index
/// dead weight. This attribute records that judgment where the model is defined.
/// </para>
/// <para>
/// It exists as an attribute rather than as a <c>#pragma warning disable</c> for two reasons. A
/// pragma only silences the compiler, so a team that suppressed the warning would still be told
/// about the same field by the maintenance cycle every interval, forever. And a pragma travels
/// with a call site, while the decision belongs to the model, which is the thing that gets copied
/// between services.
/// </para>
/// <para>
/// <strong>Where to put it.</strong> On a property, it exempts that one field. On the model, it
/// exempts every field of that perspective. On the assembly, it exempts every perspective the
/// assembly declares, which is the blunt instrument and should carry a reason that says why the
/// whole assembly is exempt.
/// </para>
/// <para>
/// <strong>A blank reason does not suppress anything.</strong> An empty or whitespace-only reason
/// is treated as no attribute at all and the advisory still fires. The attribute's value is the
/// stated rationale, so a placeholder would defeat it.
/// </para>
/// <para>
/// Teams that build with <c>TreatWarningsAsErrors</c> and want a fleet-wide policy instead of a
/// per-model decision can still reach for the standard knobs, which this attribute does not
/// replace: <c>dotnet_diagnostic.WHIZ302.severity</c> in an <c>.editorconfig</c>, or
/// <c>&lt;WarningsNotAsErrors&gt;WHIZ302&lt;/WarningsNotAsErrors&gt;</c> in a project file. Those
/// change the severity everywhere; this attribute answers one field at a time and keeps the
/// runtime advisory in step with the build.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#index-advisories</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/PerspectiveFilterIndexAnalyzerTests.cs</tests>
/// <example>
/// <code>
/// // One field: the lookup is rare and the table is small.
/// public record TenantSettingsModel {
///   [StreamId]
///   public Guid TenantId { get; init; }
///
///   [SuppressIndexAdvisory("one row per tenant; a scan of this table is cheaper than the index")]
///   public string Region { get; init; } = string.Empty;
/// }
///
/// // The whole model.
/// [SuppressIndexAdvisory("bounded at a few hundred rows by the retention cap")]
/// public record FeatureFlagModel {
///   public string Name { get; init; } = string.Empty;
/// }
/// </code>
/// </example>
/// <param name="reason">
/// Why a scan is acceptable here. Empty or whitespace-only text does not suppress the advisory.
/// </param>
[AttributeUsage(
  AttributeTargets.Property |
  AttributeTargets.Class |
  AttributeTargets.Struct |
  AttributeTargets.Assembly,
  AllowMultiple = false,
  Inherited = true)]
public sealed class SuppressIndexAdvisoryAttribute(string reason) : Attribute {
  /// <summary>
  /// Why this field, model, or assembly is queried without an index.
  /// </summary>
  public string Reason { get; } = reason;
}
