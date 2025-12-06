#!/usr/bin/env python3
"""
Fix MessageHop instantiations in benchmark files.
Simpler approach: find and replace the old pattern directly.
"""
import re
from pathlib import Path

def fix_file(file_path):
    """Fix a single benchmark file."""
    try:
        with open(file_path, 'r', encoding='utf-8') as f:
            content = f.read()

        original = content
        changes = 0

        # Pattern 1: Simple ServiceName only
        # new MessageHop { ServiceName = "value", <other fields> }
        pattern1 = r'new\s+MessageHop\s*\{\s*ServiceName\s*=\s*([^,\n]+)\s*,\s*'
        def replace1(m):
            service_name = m.group(1).strip()
            return f'''new MessageHop {{
      ServiceInstance = new ServiceInstanceInfo {{
        ServiceName = {service_name},
        InstanceId = Guid.NewGuid(),
        HostName = "benchmark-host",
        ProcessId = 12345
      }},
      '''

        content, count1 = re.subn(pattern1, replace1, content)
        changes += count1

        # Pattern 2: ServiceName with closing brace (no other fields)
        # new MessageHop { ServiceName = "value" }
        pattern2 = r'new\s+MessageHop\s*\{\s*ServiceName\s*=\s*([^,\n}]+)\s*\}'
        def replace2(m):
            service_name = m.group(1).strip()
            return f'''new MessageHop {{
      ServiceInstance = new ServiceInstanceInfo {{
        ServiceName = {service_name},
        InstanceId = Guid.NewGuid(),
        HostName = "benchmark-host",
        ProcessId = 12345
      }}
    }}'''

        content, count2 = re.subn(pattern2, replace2, content)
        changes += count2

        if changes > 0 and content != original:
            with open(file_path, 'w', encoding='utf-8') as f:
                f.write(content)
            print(f"✓ {file_path}: {changes} MessageHop(s) fixed")
            return changes

        return 0
    except Exception as e:
        print(f"✗ {file_path}: Error - {e}")
        return 0

def main():
    """Fix all benchmark files."""
    benchmarks_dir = Path('/Users/philcarbone/src/whizbang/benchmarks')

    total_changes = 0
    files_changed = 0

    for bench_file in benchmarks_dir.rglob('*.cs'):
        changes = fix_file(bench_file)
        if changes > 0:
            total_changes += changes
            files_changed += 1

    print(f"\nSummary: {files_changed} files changed, {total_changes} total MessageHop instantiations fixed")

if __name__ == '__main__':
    main()
