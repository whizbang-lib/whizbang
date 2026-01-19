Load source generator context and documentation.

Read these files to understand generator development:
- src/Whizbang.Generators/ai-docs/README.md - Navigation hub
- src/Whizbang.Generators/ai-docs/architecture.md - Multiple Independent Generators Pattern
- src/Whizbang.Generators/ai-docs/performance-principles.md - CRITICAL performance patterns
- src/Whizbang.Generators/ai-docs/value-type-records.md - CRITICAL caching pattern

Most important principles:
1. **Sealed records for caching** - NEVER use classes (breaks caching, 50-200ms overhead every build)
2. **Syntactic filtering first** - Filter 95%+ of nodes before semantic analysis
3. **Early null returns** - Exit transform as soon as you know node doesn't match
4. **Static methods** - Use `static` for predicates and transforms

For specific topics, read:
- generator-patterns.md - Three core patterns (single, parallel, post-init)
- template-system.md - Real C# templates with IDE support
- testing-strategy.md - Unit, integration, snapshot tests
- common-pitfalls.md - Seven major mistakes to avoid

Use this command when:
- Working on source generators
- Debugging generator performance
- Creating new generators
