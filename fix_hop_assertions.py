#!/usr/bin/env python3
"""
Fix assertions and references to MessageHop fields that moved to ServiceInstance.
"""
import re
from pathlib import Path

def fix_assertions(content):
    """
    Fix references to MessageHop fields that are now nested in ServiceInstance.
    """
    changes = 0

    # Pattern: hop.ServiceName -> hop.ServiceInstance.ServiceName
    patterns = [
        (r'\.ServiceName\b', '.ServiceInstance.ServiceName'),
        (r'\.InstanceId\b', '.ServiceInstance.InstanceId'),
        (r'\.HostName\b', '.ServiceInstance.HostName'),
        (r'\.ProcessId\b', '.ServiceInstance.ProcessId'),
        (r'\.MachineName\b', '.ServiceInstance.HostName'),  # MachineName was renamed to HostName
    ]

    new_content = content
    for old_pattern, replacement in patterns:
        new_content, count = re.subn(old_pattern, replacement, new_content)
        changes += count

    return new_content, changes

def process_file(file_path):
    """Process a single file."""
    try:
        with open(file_path, 'r', encoding='utf-8') as f:
            content = f.read()

        new_content, changes = fix_assertions(content)

        if changes > 0:
            with open(file_path, 'w', encoding='utf-8') as f:
                f.write(new_content)
            print(f"✓ {file_path}: {changes} assertion(s) fixed")
            return changes

        return 0
    except Exception as e:
        print(f"✗ {file_path}: Error - {e}")
        return 0

def main():
    """Find and process all test files."""
    test_dir = Path('/Users/philcarbone/src/whizbang/tests')

    total_changes = 0
    files_changed = 0

    for test_file in test_dir.rglob('*.cs'):
        changes = process_file(test_file)
        if changes > 0:
            total_changes += changes
            files_changed += 1

    print(f"\nSummary: {files_changed} files changed, {total_changes} total assertions fixed")

if __name__ == '__main__':
    main()
