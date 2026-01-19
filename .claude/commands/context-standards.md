Load code standards and quality guidelines.

Read these files to understand standards:
- ai-docs/code-standards.md - Formatting, naming, dotnet format (MANDATORY)
- ai-docs/boy-scout-rule.md - Leave code better than you found it
- ai-docs/aot-requirements.md - Zero reflection, native AOT

Key standards:
1. **dotnet format** - MANDATORY before every commit
2. **Async naming** - ALL async methods end with "Async" (including tests)
3. **UUIDv7** - Use `Guid.CreateVersion7()` for all IDs (not `Guid.NewGuid()`)
4. **File-scoped namespaces** - Use `namespace Foo;` not `namespace Foo { }`
5. **XML documentation** - Required on all public APIs

Non-negotiable rules:
- ✅ Run `dotnet format` before every commit
- ✅ All async methods end with "Async"
- ✅ Use sealed records for value types (not classes)
- ✅ Zero reflection in library code (AOT compatible)
- ✅ Boy Scout Rule in REFACTOR phase

Use this command when:
- Starting new development
- Code review preparation
- Need standards reminder
