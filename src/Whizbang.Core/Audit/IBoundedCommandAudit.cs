// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Audit;

/// <summary>
/// A command that carries id lists of unbounded length. Its command audit record
/// (<see cref="Whizbang.Core.SystemEvents.CommandAudited.CommandBody"/>) keeps the length of each list it names, not
/// the list, so auditing a command that names a hundred thousand streams writes a number instead of megabytes of ids.
/// </summary>
/// <remarks>
/// <para>
/// Each named property whose serialized value is a JSON array is replaced in the audit record by its length, a JSON
/// number. A null list stays null and every other property is kept as serialized. Names are the C# property names,
/// compared with the serialized names without regard to case, so a camel-case naming policy changes nothing.
/// </para>
/// <para>
/// A method rather than a property, so the names are never serialized with the command. Return a cached collection:
/// it is read once per audited command.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/audit-logging#bounded-command-audit</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/CommandAuditListLengthsTests.cs</tests>
public interface IBoundedCommandAudit {
  /// <summary>The properties whose lists a command audit record replaces with their lengths.</summary>
  /// <returns>The C# property names of the command's id lists.</returns>
  IReadOnlyCollection<string> ListsAuditedAsLengths();
}
