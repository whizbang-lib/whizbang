// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Transports.FastEndpoints.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for the REST lens endpoint generator.
/// </summary>
/// <remarks>
/// A source generator fails by producing nothing. There is no exception and no build error --
/// the consumer's REST endpoint simply does not exist, and the first sign of it is a 404 in an
/// environment where someone expected an API. So these tests assert both directions: what the
/// generator emits for a valid declaration, and that it stays silent for declarations it cannot
/// legitimately serve.
/// </remarks>
/// <tests>Whizbang.Transports.FastEndpoints.Generators/RestLensEndpointGenerator.cs:*</tests>
public class RestLensEndpointGeneratorTests {

  private const string LENS_QUERY_STUB = """
    namespace Whizbang.Core.Lenses {
      public interface ILensQuery { }
      public interface ILensQuery<TModel> : ILensQuery where TModel : class { }
    }
    namespace Whizbang.Transports.FastEndpoints {
      [System.AttributeUsage(System.AttributeTargets.Interface | System.AttributeTargets.Class)]
      public sealed class RestLensAttribute : System.Attribute {
        public string? Route { get; set; }
        public bool EnableFiltering { get; set; } = true;
        public bool EnableSorting { get; set; } = true;
        public bool EnablePaging { get; set; } = true;
        public int DefaultPageSize { get; set; } = 10;
        public int MaxPageSize { get; set; } = 100;
      }
    }
    namespace App {
      public class Order { }
    }
    """;

  private static string _generatedSource(string declaration) {
    var result = GeneratorTestHelper.RunGenerator<RestLensEndpointGenerator>(
      LENS_QUERY_STUB + "\n" + declaration);
    return string.Concat(result.Results
      .SelectMany(r => r.GeneratedSources)
      .Select(s => s.SourceText.ToString()));
  }

  [Test]
  public async Task Generator_LensWithTheAttribute_EmitsAnEndpointAsync() {
    var generated = _generatedSource("""
      namespace App {
        using Whizbang.Core.Lenses;
        using Whizbang.Transports.FastEndpoints;

        [RestLens(Route = "/api/orders")]
        public interface IOrderLens : ILensQuery<Order> { }
      }
      """);

    await Assert.That(generated).IsNotEmpty()
      .Because("a lens marked [RestLens] is the entire trigger for generating its endpoint");
    await Assert.That(generated).Contains("/api/orders");
  }

  [Test]
  public async Task Generator_WithoutTheAttribute_EmitsNothingAsync() {
    // A lens is an ordinary query type until someone opts it into HTTP. Generating an endpoint
    // for every lens would publish query surfaces nobody asked to expose.
    var generated = _generatedSource("""
      namespace App {
        using Whizbang.Core.Lenses;

        public interface IOrderLens : ILensQuery<Order> { }
      }
      """);

    await Assert.That(generated).IsEmpty();
  }

  [Test]
  public async Task Generator_AttributeWithoutILensQuery_EmitsNothingAsync() {
    // The model type comes from ILensQuery<TModel>. Without it there is nothing to bind the
    // endpoint to, so emitting anyway would produce source that cannot compile -- which is a
    // worse outcome than declining, because it breaks the whole build rather than one route.
    var generated = _generatedSource("""
      namespace App {
        using Whizbang.Transports.FastEndpoints;

        [RestLens(Route = "/api/orders")]
        public interface INotALens { }
      }
      """);

    await Assert.That(generated).IsEmpty();
  }

  [Test]
  public async Task Generator_WithoutAnExplicitRoute_DerivesOneFromTheModelAsync() {
    // Route is optional, so the fallback is what most consumers actually get. If it silently
    // produced an empty route, every such endpoint would collide at "/".
    var generated = _generatedSource("""
      namespace App {
        using Whizbang.Core.Lenses;
        using Whizbang.Transports.FastEndpoints;

        [RestLens]
        public interface IOrderLens : ILensQuery<Order> { }
      }
      """);

    await Assert.That(generated).IsNotEmpty();
    await Assert.That(generated).Contains("/api/order")
      .Because("the default route is derived from the model type name, not left blank");
  }

  [Test]
  public async Task Generator_CarriesThePagingBoundsIntoTheEndpointAsync() {
    // MaxPageSize is a denial-of-service guard: it caps what a caller can request. A generator
    // that dropped it would emit an endpoint that honors any page size a client asks for.
    var generated = _generatedSource("""
      namespace App {
        using Whizbang.Core.Lenses;
        using Whizbang.Transports.FastEndpoints;

        [RestLens(Route = "/api/orders", DefaultPageSize = 25, MaxPageSize = 250)]
        public interface IOrderLens : ILensQuery<Order> { }
      }
      """);

    await Assert.That(generated).Contains("25");
    await Assert.That(generated).Contains("250");
  }

  [Test]
  public async Task Generator_EmitsIntoTheExpectedFileAsync() {
    // The file name is the handle anyone debugging generated output looks for.
    var result = GeneratorTestHelper.RunGenerator<RestLensEndpointGenerator>(
      LENS_QUERY_STUB + """
      namespace App {
        using Whizbang.Core.Lenses;
        using Whizbang.Transports.FastEndpoints;

        [RestLens(Route = "/api/orders")]
        public interface IOrderLens : ILensQuery<Order> { }
      }
      """);

    var hints = result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.HintName).ToList();
    await Assert.That(hints).Contains("WhizbangRestLensEndpoints.g.cs");
  }

  [Test]
  public async Task Generator_OnAnEmptyCompilation_EmitsNothingAndDoesNotThrowAsync() {
    // Most projects contain no lenses at all. The generator runs on every one of them.
    var result = GeneratorTestHelper.RunGenerator<RestLensEndpointGenerator>(
      "namespace App { public class Nothing { } }");

    await Assert.That(result.Results.SelectMany(r => r.GeneratedSources)).IsEmpty();
    await Assert.That(result.Diagnostics.Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
      .IsFalse();
  }

  // ========================================
  // #1194: EnableFiltering, EnableSorting and EnablePaging decide what the endpoint does
  // ========================================

  private const string SHAPED_MODEL = """
    namespace App {
      using System;
      using System.Collections.Generic;

      public enum Status { Open, Closed }

      public class Address { public string? City { get; set; } }

      public class AuditedModel { public DateTimeOffset CreatedAt { get; set; } }

      public class Invoice : AuditedModel {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public decimal Amount { get; set; }
        public Status State { get; set; }
        public int? Priority { get; set; }
        public bool Paid { get; set; }
        public List<string> Tags { get; set; } = new();
        public Address? Address { get; set; }
        public static int Ignored { get; set; }
        public string WriteOnly { set { } }
      }
    }
    """;

  private static string _invoiceEndpoint(string attributeArguments) => _generatedSource(SHAPED_MODEL + $$"""
    namespace App {
      using Whizbang.Core.Lenses;
      using Whizbang.Transports.FastEndpoints;

      [RestLens({{attributeArguments}})]
      public interface IInvoiceLens : ILensQuery<Invoice> { }
    }
    """);

  [Test]
  public async Task Generator_ByDefault_AppliesFilteringSortingAndPagingAsync() {
    var generated = _invoiceEndpoint("Route = \"/api/invoices\"");

    await Assert.That(generated).Contains("req.Filter")
      .Because("filtering is on by default, so the endpoint reads the filter parameters");
    await Assert.That(generated).Contains("LensQueryShaping.ParseSort(req.Sort)")
      .Because("sorting is on by default, so the endpoint reads the sort parameter");
    await Assert.That(generated).Contains(".Skip(skip).Take(pageSize)")
      .Because("paging is on by default");
    await Assert.That(generated).DoesNotContain("TODO");
  }

  [Test]
  public async Task Generator_WithFilteringDisabled_IgnoresFilterParametersAsync() {
    var generated = _invoiceEndpoint("Route = \"/api/invoices\", EnableFiltering = false");

    await Assert.That(generated).DoesNotContain("req.Filter");
    await Assert.That(generated).DoesNotContain("_filter(");
    await Assert.That(generated).Contains("LensQueryShaping.ParseSort(req.Sort)");
  }

  [Test]
  public async Task Generator_WithSortingDisabled_OrdersByIdOnlyAsync() {
    var generated = _invoiceEndpoint("Route = \"/api/invoices\", EnableSorting = false");

    await Assert.That(generated).DoesNotContain("req.Sort");
    await Assert.That(generated).DoesNotContain("_sort(");
    await Assert.That(generated).Contains(".OrderBy(x => x.Id)")
      .Because("an unsorted endpoint still needs a stable order for its pages");
    await Assert.That(generated).Contains("req.Filter");
  }

  [Test]
  public async Task Generator_WithPagingDisabled_ReturnsEveryRowAsync() {
    var generated = _invoiceEndpoint("Route = \"/api/invoices\", EnablePaging = false");

    await Assert.That(generated).DoesNotContain(".Skip(");
    await Assert.That(generated).DoesNotContain("req.Page");
    await Assert.That(generated).Contains("PageSize = items.Count");
  }

  [Test]
  public async Task Generator_FiltersEachScalarPropertyByItsTypeAsync() {
    var generated = _invoiceEndpoint("Route = \"/api/invoices\"");

    await Assert.That(generated).Contains("case \"NAME\": return query.Where(x => x.Name == value);")
      .Because("text compares as given");
    await Assert.That(generated).Contains("LensQueryShaping.Parse<decimal>(field, value)");
    await Assert.That(generated).Contains("LensQueryShaping.Parse<global::System.Guid>(field, value)");
    await Assert.That(generated).Contains("LensQueryShaping.Parse<int>(field, value)")
      .Because("a nullable property is filtered by its underlying type");
    await Assert.That(generated).Contains("LensQueryShaping.Parse<bool>(field, value)");
    await Assert.That(generated).Contains("LensQueryShaping.ParseEnum<global::App.Status>(field, value)");
    await Assert.That(generated).Contains("case \"CREATEDAT\":")
      .Because("an inherited property is part of the model");
    await Assert.That(generated).Contains("throw InvalidLensRequestException.UnknownField(\"filter\", field)");
  }

  [Test]
  public async Task Generator_SortsEachScalarPropertyAsync() {
    var generated = _invoiceEndpoint("Route = \"/api/invoices\"");

    await Assert.That(generated).Contains("\"AMOUNT\" => LensQueryShaping.ThenOrderBy(query, ordered, x => x.Amount, sort.Descending),");
    await Assert.That(generated).Contains("\"STATE\" => LensQueryShaping.ThenOrderBy(query, ordered, x => x.State, sort.Descending),");
    await Assert.That(generated).Contains("_ => throw InvalidLensRequestException.UnknownField(\"sort\", sort.Field),");
  }

  [Test]
  public async Task Generator_LeavesOutWhatNeitherFiltersNorSortsAsync() {
    var generated = _invoiceEndpoint("Route = \"/api/invoices\"");

    await Assert.That(generated).DoesNotContain("x.Tags").Because("a collection has no single value to compare or order by");
    await Assert.That(generated).DoesNotContain("x.Address").Because("a nested object has no single value either");
    await Assert.That(generated).DoesNotContain("x.Ignored").Because("a static property is not part of a row");
    await Assert.That(generated).DoesNotContain("x.WriteOnly").Because("a property with no getter cannot be read");
  }

  /// <summary>
  /// The endpoint reads through the lens's default scope, so a public endpoint returns only the rows
  /// the caller's scope allows; the unscoped legacy query bypassed that and is obsolete.
  /// </summary>
  [Test]
  public async Task Generator_ReadsThroughTheLensDefaultScopeAsync() {
    var generated = _invoiceEndpoint("Route = \"/api/invoices\"");

    await Assert.That(generated).Contains("_lens.DefaultScope.Query.Select(r => r.Data)");
    await Assert.That(generated).DoesNotContain("_lens.Query");
  }

  /// <summary>The response goes out through the current FastEndpoints send API.</summary>
  [Test]
  public async Task Generator_SendsTheResponseThroughTheSendApiAsync() {
    var generated = _invoiceEndpoint("Route = \"/api/invoices\"");

    await Assert.That(generated).Contains("await Send.OkAsync(response, ct);");
  }

  // ========================================
  // [FieldPermission]: protected members are masked in responses and cannot filter or sort
  // ========================================

  private const string FIELD_PERMISSION_STUB = """
    namespace Whizbang.Core.Security.Attributes {
      public enum MaskingStrategy { Hide = 0, Mask = 1, Partial = 2, Redact = 3 }

      [System.AttributeUsage(System.AttributeTargets.Property)]
      public sealed class FieldPermissionAttribute : System.Attribute {
        public FieldPermissionAttribute(string permission, MaskingStrategy masking = MaskingStrategy.Hide) { }
      }
    }
    namespace App {
      using System.Collections.Generic;
      using System.Text.Json.Serialization;
      using Whizbang.Core.Security.Attributes;

      public class Street { [FieldPermission("pii:view", MaskingStrategy.Mask)] public string? Line { get; set; } }

      public class Contact {
        public string? Kind { get; set; }
        [FieldPermission("pii:view", MaskingStrategy.Redact)] public string? Value { get; set; }
        public Street? Street { get; set; }
      }

      public class Node { public string? Label { get; set; } public Node? Next { get; set; } }

      public struct Coordinates { [FieldPermission("geo:view", MaskingStrategy.Mask)] public string? Grid { get; set; } }

      public enum Ward { North, South }

      public class PersonBase {
        [FieldPermission("pii:view")] public virtual string? Secret { get; set; }
      }

      public class Patient : PersonBase {
        public System.Guid Id { get; set; }
        public string? Name { get; set; }
        [FieldPermission("pii:view", MaskingStrategy.Partial)] public string? Ssn { get; set; }
        [FieldPermission("finance:view", MaskingStrategy.Mask)] public decimal Balance { get; set; }
        [FieldPermission("pii:view", MaskingStrategy.Redact)]
        [JsonPropertyName("tax_id")]
        public string? TaxId { get; set; }
        public override string? Secret { get; set; }
        public List<Contact> Contacts { get; set; } = new();
        public Street[] Streets { get; set; } = new Street[0];
        public Node? Head { get; set; }
        public PersonBase? Guardian { get; set; }
        public Coordinates Location { get; set; }
        public Ward Ward { get; set; }
        public dynamic? Bag { get; set; }
        [FieldPermission("pii:view")] public static string? Shared { get; set; }
        [FieldPermission("pii:view")] public string? this[int index] => null;
        [FieldPermission("pii:view")] public string? WriteOnly { set { } }
        [FieldPermission("pii:view")] internal string? Internal { get; set; }
      }
    }
    """;

  private static string _patientEndpoint() => _generatedSource(FIELD_PERMISSION_STUB + """
    namespace App {
      using Whizbang.Core.Lenses;
      using Whizbang.Transports.FastEndpoints;

      [RestLens(Route = "/api/patients")]
      public interface IPatientLens : ILensQuery<Patient> { }
    }
    """);

  private const string REGISTER = "global::Whizbang.Transports.FastEndpoints.FieldPermissionJson.Register(";
  private const string ATTRIBUTE = "new global::Whizbang.Core.Security.Attributes.FieldPermissionAttribute(";
  private const string STRATEGY = "(global::Whizbang.Core.Security.Attributes.MaskingStrategy)";

  [Test]
  public async Task Generator_ProtectedMembers_CannotBeFilteredOrSortedByAsync() {
    var generated = _patientEndpoint();

    await Assert.That(generated).Contains("case \"NAME\":").Because("an unprotected member still filters");
    await Assert.That(generated).Contains("\"NAME\" => LensQueryShaping.ThenOrderBy(");
    foreach (var key in new[] { "SSN", "BALANCE", "TAXID", "SECRET" }) {
      await Assert.That(generated).DoesNotContain($"case \"{key}\":")
        .Because("a filter on a protected member would reveal its value");
      await Assert.That(generated).DoesNotContain($"\"{key}\" =>")
        .Because("an ordering by a protected member would reveal its value");
    }
  }

  [Test]
  public async Task Generator_RegistersEachProtectedMemberForResponseMaskingAsync() {
    var generated = _patientEndpoint();

    await Assert.That(generated).Contains("[global::System.Runtime.CompilerServices.ModuleInitializer]");
    await Assert.That(generated).Contains($"{REGISTER}typeof(global::App.Patient), \"Ssn\", null, {ATTRIBUTE}\"pii:view\", {STRATEGY}2), true);");
    await Assert.That(generated).Contains($"{REGISTER}typeof(global::App.Patient), \"Balance\", null, {ATTRIBUTE}\"finance:view\", {STRATEGY}1), false);")
      .Because("a non-string member is registered as one, so it is hidden rather than given a placeholder");
    await Assert.That(generated).Contains($"{REGISTER}typeof(global::App.Patient), \"TaxId\", \"tax_id\", {ATTRIBUTE}\"pii:view\", {STRATEGY}3), true);")
      .Because("an explicit JSON name is the name the response carries");
    await Assert.That(generated).Contains($"{REGISTER}typeof(global::App.PersonBase), \"Secret\", null, {ATTRIBUTE}\"pii:view\", {STRATEGY}0), true);")
      .Because("an inherited member is registered on the type that declares it, with the default strategy when none is given");
    await Assert.That(generated).DoesNotContain("\"Name\", null");
  }

  [Test]
  public async Task Generator_RegistersProtectedMembersOfNestedTypesAsync() {
    var generated = _patientEndpoint();

    await Assert.That(generated).Contains($"{REGISTER}typeof(global::App.Contact), \"Value\", null,")
      .Because("a list's element type is part of the response");
    await Assert.That(generated).Contains($"{REGISTER}typeof(global::App.Street), \"Line\", null,")
      .Because("types nested inside nested types, and array element types, are part of the response");
    await Assert.That(generated).Contains($"{REGISTER}typeof(global::App.Coordinates), \"Grid\", null,")
      .Because("a struct of the application's is part of the response");
    await Assert.That(generated.Split($"{REGISTER}typeof(global::App.Street)").Length - 1).IsEqualTo(1)
      .Because("a type reached twice is registered once");
    await Assert.That(generated.Split($"{REGISTER}typeof(global::App.PersonBase)").Length - 1).IsEqualTo(1)
      .Because("a base type reached both directly and through a derived type is registered once");
  }

  [Test]
  public async Task Generator_RegistersOnlyMembersAResponseCarriesAsync() {
    var generated = _patientEndpoint();

    await Assert.That(generated).DoesNotContain("\"Shared\"").Because("a static property is not part of a row");
    await Assert.That(generated).DoesNotContain("\"this[]\"").Because("an indexer is not serialized");
    await Assert.That(generated).DoesNotContain("\"Item\"").Because("an indexer is not serialized");
    await Assert.That(generated).DoesNotContain("\"WriteOnly\"").Because("a property with no getter is not serialized");
    await Assert.That(generated).DoesNotContain("\"Internal\"").Because("a non-public property is not serialized");
  }

  [Test]
  public async Task Generator_TwoLensesOverOneModel_RegisterItsMembersOnceAsync() {
    var generated = _generatedSource(FIELD_PERMISSION_STUB + """
      namespace App {
        using Whizbang.Core.Lenses;
        using Whizbang.Transports.FastEndpoints;

        [RestLens(Route = "/api/patients")]
        public interface IPatientLens : ILensQuery<Patient> { }

        [RestLens(Route = "/api/admin/patients")]
        public interface IAdminPatientLens : ILensQuery<Patient> { }
      }
      """);

    await Assert.That(generated.Split($"{REGISTER}typeof(global::App.Patient), \"Ssn\"").Length - 1).IsEqualTo(1);
    await Assert.That(generated.Split("FieldPermissionJson.EnsureMasked(typeof(global::App.Patient));").Length - 1).IsEqualTo(2)
      .Because("each endpoint checks before it responds");
  }

  [Test]
  public async Task Generator_ProtectedModel_ChecksTheResponseIsMaskedBeforeQueryingAsync() {
    var generated = _patientEndpoint();

    await Assert.That(generated).Contains("global::Whizbang.Transports.FastEndpoints.FieldPermissionJson.EnsureMasked(typeof(global::App.Patient));");
  }

  [Test]
  public async Task Generator_ModelWithoutProtectedMembers_RegistersNothingAsync() {
    var generated = _invoiceEndpoint("Route = \"/api/invoices\"");

    await Assert.That(generated).DoesNotContain("FieldPermissionJson");
    await Assert.That(generated).DoesNotContain("ModuleInitializer");
  }
}
