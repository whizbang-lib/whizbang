// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Third-party GUID libraries are not intercepted (#2528). An interceptor has the intercepted method's signature, so it
/// returns what the original returns and cannot attach metadata; the libraries below already return time-ordered ids,
/// so a pass-through interceptor would change nothing, and the earlier generated ones did not compile (a
/// <c>TrackedGuid</c> returned where a <c>Guid</c> or <c>Uuid7</c> was expected, a parameter dropped). With interception
/// on by default, a project that calls them must still build.
/// </summary>
[Category("Generators")]
[Category("Interceptors")]
[Category("ThirdParty")]
public class ThirdPartyGuidInterceptionTests {
  private const string MARTEN = """
    using System;

    namespace Marten.Schema.Identity {
      public static class CombGuidIdGeneration {
        public static Guid NewGuid() => default;
      }
    }

    namespace TestApp {
      public class MyService {
        public Guid Create() => Marten.Schema.Identity.CombGuidIdGeneration.NewGuid();
      }
    }
    """;

  private const string MEDO = """
    using System;

    namespace Medo {
      public readonly struct Uuid7 {
        public static Uuid7 NewUuid7() => default;
        public Guid ToGuid() => Guid.Empty;
      }
    }

    namespace TestApp {
      public class MyService {
        public Guid Create() => Medo.Uuid7.NewUuid7().ToGuid();
      }
    }
    """;

  private const string UUIDNEXT_DATABASE_FRIENDLY = """
    using System;

    namespace UUIDNext {
      public enum Database { SqlServer, PostgreSql, MySql }
      public static class Uuid {
        public static Guid NewDatabaseFriendly(Database db) => default;
      }
    }

    namespace TestApp {
      public class MyService {
        public Guid Create() => UUIDNext.Uuid.NewDatabaseFriendly(UUIDNext.Database.PostgreSql);
      }
    }
    """;

  private const string UUIDNEXT_SEQUENTIAL = """
    using System;

    namespace UUIDNext {
      public static class Uuid {
        public static Guid NewSequential() => default;
      }
    }

    namespace TestApp {
      public class MyService {
        public Guid Create() => UUIDNext.Uuid.NewSequential();
      }
    }
    """;

  public static IEnumerable<Func<string>> Libraries() {
    yield return () => MARTEN;
    yield return () => MEDO;
    yield return () => UUIDNEXT_DATABASE_FRIENDLY;
    yield return () => UUIDNEXT_SEQUENTIAL;
  }

  /// <summary>A third-party library's call gets no interceptor and is not reported as intercepted.</summary>
  [Test]
  [RequiresAssemblyFiles]
  [MethodDataSource(nameof(Libraries))]
  public async Task Generator_ThirdPartyCall_IsNotInterceptedAsync(string source) {
    var result = GeneratorTestHelper.RunGenerator<GuidInterceptorGenerator>(source);

    await Assert.That(GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs")).IsNull();
    await Assert.That(result.Diagnostics.Count(d => d.Id == "WHIZ058")).IsEqualTo(0);
  }

  /// <summary>With interception on by default, a project calling each library compiles.</summary>
  [Test]
  [RequiresAssemblyFiles]
  [MethodDataSource(nameof(Libraries))]
  public async Task Generator_ThirdPartyCall_CompilesWithInterceptionOnAsync(string source) {
    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors<GuidInterceptorGenerator>(source);

    await Assert.That(errors.Select(e => e.ToString())).IsEmpty();
  }

  /// <summary>Beside third-party calls, only <c>Guid.NewGuid()</c> is intercepted.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_MixedWithSystemGuid_InterceptsOnlySystemGuidAsync() {
    const string source = """
      using System;

      namespace Marten.Schema.Identity {
        public static class CombGuidIdGeneration {
          public static Guid NewGuid() => default;
        }
      }

      namespace TestApp {
        public class MyService {
          public Guid CreateMartenId() => Marten.Schema.Identity.CombGuidIdGeneration.NewGuid();
          public Guid CreateSystemId() => Guid.NewGuid();
        }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<GuidInterceptorGenerator>(source);

    var generatedSource = GeneratorTestHelper.GetRequiredGeneratedSource(result, "GuidInterceptors.g.cs");
    var interceptCount = generatedSource.Split("[global::System.Runtime.CompilerServices.InterceptsLocation(").Length - 1;
    await Assert.That(interceptCount).IsEqualTo(1);
    await Assert.That(result.Diagnostics.Single(d => d.Id == "WHIZ058").GetMessage(System.Globalization.CultureInfo.InvariantCulture))
      .Contains("System.Guid.NewGuid()");
  }

  /// <summary>An extension method named <c>NewGuid</c> is not <c>Guid.NewGuid()</c>.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_GuidExtensionMethod_NotInterceptedAsync() {
    const string source = """
      using System;

      namespace TestApp;

      public static class GuidExtensions {
        public static Guid NewGuid(this string prefix) => default;
      }

      public class MyService {
        public Guid CreateId() => "test".NewGuid();
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<GuidInterceptorGenerator>(source);

    await Assert.That(GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs")).IsNull();
  }
}
