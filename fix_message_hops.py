#!/usr/bin/env python3
"""
Fix MessageHop instantiations to use ServiceInstance pattern.
"""
import re
import sys
from pathlib import Path

def fix_message_hop(content):
    """
    Transform MessageHop instantiations from old pattern to new pattern.

    Old: new MessageHop { ServiceName = "x", InstanceId = y, ... }
    New: new MessageHop { ServiceInstance = new ServiceInstanceInfo { ... }, ... }
    """
    changes = 0

    # Pattern to match MessageHop initialization with ServiceName field
    # This handles both inline and multi-line patterns
    pattern = r'new\s+MessageHop\s*\{'

    lines = content.split('\n')
    result = []
    i = 0

    while i < len(lines):
        line = lines[i]

        # Check if this line starts a MessageHop initialization
        if re.search(pattern, line):
            # Collect the full MessageHop block
            block_lines = [line]
            brace_count = line.count('{') - line.count('}')
            j = i + 1

            while j < len(lines) and brace_count > 0:
                block_lines.append(lines[j])
                brace_count += lines[j].count('{') - lines[j].count('}')
                j += 1

            block = '\n'.join(block_lines)

            # Check if this block has old-style ServiceName field
            if re.search(r'ServiceName\s*=', block):
                # Extract indentation
                indent_match = re.match(r'^(\s*)', block_lines[0])
                base_indent = indent_match.group(1) if indent_match else ''
                field_indent = base_indent + '  '
                instance_indent = field_indent + '  '

                # Extract the fields
                service_name_match = re.search(r'ServiceName\s*=\s*([^,\n]+)', block)
                instance_id_match = re.search(r'InstanceId\s*=\s*([^,\n]+)', block)
                host_name_match = re.search(r'(?:HostName|MachineName)\s*=\s*([^,\n]+)', block)
                process_id_match = re.search(r'ProcessId\s*=\s*([^,\n]+)', block)

                service_name = service_name_match.group(1).strip() if service_name_match else '"TestService"'
                instance_id = instance_id_match.group(1).strip() if instance_id_match else 'Guid.NewGuid()'
                host_name = host_name_match.group(1).strip() if host_name_match else '"test-host"'
                process_id = process_id_match.group(1).strip() if process_id_match else '12345'

                # Remove trailing commas
                service_name = service_name.rstrip(',')
                instance_id = instance_id.rstrip(',')
                host_name = host_name.rstrip(',')
                process_id = process_id.rstrip(',')

                # Remove the old fields from block
                block = re.sub(r'\s*ServiceName\s*=\s*[^,\n]+,?\n?', '', block)
                block = re.sub(r'\s*InstanceId\s*=\s*[^,\n]+,?\n?', '', block)
                block = re.sub(r'\s*(?:HostName|MachineName)\s*=\s*[^,\n]+,?\n?', '', block)
                block = re.sub(r'\s*ProcessId\s*=\s*[^,\n]+,?\n?', '', block)

                # Build ServiceInstance block
                service_instance_block = f'{field_indent}ServiceInstance = new ServiceInstanceInfo {{\n'
                service_instance_block += f'{instance_indent}ServiceName = {service_name},\n'
                service_instance_block += f'{instance_indent}InstanceId = {instance_id},\n'
                service_instance_block += f'{instance_indent}HostName = {host_name},\n'
                service_instance_block += f'{instance_indent}ProcessId = {process_id}\n'
                service_instance_block += f'{field_indent}}},\n'

                # Insert ServiceInstance after opening brace
                new_block = re.sub(
                    r'(new\s+MessageHop\s*\{\s*\n)',
                    r'\1' + service_instance_block,
                    block,
                    count=1
                )

                result.append(new_block)
                changes += 1
                i = j
                continue

        result.append(line)
        i += 1

    return '\n'.join(result), changes

def process_file(file_path):
    """Process a single file."""
    try:
        with open(file_path, 'r', encoding='utf-8') as f:
            content = f.read()

        new_content, changes = fix_message_hop(content)

        if changes > 0:
            with open(file_path, 'w', encoding='utf-8') as f:
                f.write(new_content)
            print(f"✓ {file_path}: {changes} MessageHop(s) fixed")
            return changes

        return 0
    except Exception as e:
        print(f"✗ {file_path}: Error - {e}", file=sys.stderr)
        return 0

def main():
    """Find and process all test files."""
    test_dir = Path('/Users/philcarbone/src/whizbang/tests')

    # Skip Whizbang.Core.Tests as it was already fixed
    test_files = []
    for test_file in test_dir.rglob('*.cs'):
        if 'Whizbang.Core.Tests' not in str(test_file):
            test_files.append(test_file)

    total_changes = 0
    files_changed = 0

    for test_file in test_files:
        changes = process_file(test_file)
        if changes > 0:
            total_changes += changes
            files_changed += 1

    print(f"\nSummary: {files_changed} files changed, {total_changes} total MessageHop instantiations fixed")

if __name__ == '__main__':
    main()
