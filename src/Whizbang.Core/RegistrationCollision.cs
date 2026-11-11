// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core;

/// <summary>
/// One item two different assemblies both registered: the registration the host uses is the latest one, and the
/// earlier one is hidden.
/// </summary>
/// <param name="Registration">What was registered (the service, such as <c>IPerspectiveRunnerRegistry</c>).</param>
/// <param name="Item">The item both assemblies registered (a perspective name, a model, a receptor).</param>
/// <param name="WinningAssembly">The assembly whose registration is used: the one registered latest.</param>
/// <param name="HiddenAssembly">The assembly whose registration is hidden.</param>
/// <docs>extending/extensibility/replaceable-services#registration-collisions</docs>
/// <tests>tests/Whizbang.Core.Tests/RegistrationCollisionsTests.cs</tests>
public sealed record RegistrationCollision(string Registration, string Item, string WinningAssembly, string HiddenAssembly) {
  /// <summary>The collision as one sentence, as the startup warning and the startup error name it.</summary>
  /// <returns>The description.</returns>
  public override string ToString() =>
    $"{Registration} '{Item}' is registered by {HiddenAssembly} and by {WinningAssembly}; "
    + $"{WinningAssembly} was registered latest and is used, and {HiddenAssembly}'s is hidden";
}

/// <summary>
/// Something that knows which items more than one assembly registered. Every per-assembly registration adds one
/// through <see cref="RegistrationCollisionServiceCollectionExtensions.AddRegistrationCollisionSource"/>, and the
/// startup check reports what every source found.
/// </summary>
/// <docs>extending/extensibility/replaceable-services#registration-collisions</docs>
/// <tests>tests/Whizbang.Core.Tests/RegistrationCollisionsTests.cs</tests>
public interface IRegistrationCollisionSource {
  /// <summary>The collisions this registration has, in the order its items were registered.</summary>
  /// <returns>The collisions; empty when no two assemblies registered the same item.</returns>
  IReadOnlyList<RegistrationCollision> GetCollisions();
}

/// <summary>
/// The one rule for an item two assemblies both register: the latest registration wins, and each earlier one from a
/// different assembly is a collision.
/// </summary>
/// <remarks>
/// <para>
/// Every per-assembly registration (perspective runners, event types, lifecycle receptors, pinned identities,
/// perspective invokers, EF Core models) lets every assembly contribute, and a lookup asks the latest contribution
/// first. When two different assemblies contribute the same item, the latest one answers and the earlier one is
/// never reached: that hides a registration, so it is reported at startup instead of passing silently. An item an
/// assembly lists twice, or an assembly registered twice, is that assembly's own business and is not a collision.
/// </para>
/// </remarks>
/// <docs>extending/extensibility/replaceable-services#registration-collisions</docs>
/// <tests>tests/Whizbang.Core.Tests/RegistrationCollisionsTests.cs</tests>
public static class RegistrationCollisions {
  /// <summary>
  /// The collisions among <paramref name="contributions"/>, given in the order they were registered.
  /// </summary>
  /// <param name="registration">What was registered, as the warning names it.</param>
  /// <param name="contributions">Each contribution's assembly and items, earliest registered first.</param>
  /// <returns>One collision per (item, hidden assembly), the latest contribution's items first.</returns>
  /// <tests>tests/Whizbang.Core.Tests/RegistrationCollisionsTests.cs:TheLatestAssemblyWins_EachEarlierAssemblyIsHiddenAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/RegistrationCollisionsTests.cs:AnItemOneAssemblyListsTwice_IsNotACollisionAsync</tests>
  public static IReadOnlyList<RegistrationCollision> Find(
      string registration, IEnumerable<(string Assembly, IEnumerable<string> Items)> contributions) {
    ArgumentNullException.ThrowIfNull(registration);
    ArgumentNullException.ThrowIfNull(contributions);
    var winners = new Dictionary<string, string>(StringComparer.Ordinal);
    var reported = new HashSet<(string Item, string Hidden)>();
    var collisions = new List<RegistrationCollision>();
    foreach (var (assembly, items) in contributions.Reverse()) {
      foreach (var item in items) {
        if (!winners.TryGetValue(item, out var winner)) {
          winners[item] = assembly;
        } else if (winner != assembly && reported.Add((item, assembly))) {
          collisions.Add(new RegistrationCollision(registration, item, winner, assembly));
        }
      }
    }
    return collisions;
  }
}
