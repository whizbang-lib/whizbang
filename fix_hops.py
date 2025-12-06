#!/usr/bin/env python3
import re

def fix_simple_hop(match):
    """Fix simple single-line MessageHop instantiations"""
    indent = match.group(1)
    service_name = match.group(2)
    rest = match.group(3) if match.group(3) else ""

    return f'''{indent}new MessageHop {{
{indent}  ServiceInstance = new ServiceInstanceInfo {{
{indent}    ServiceName = "{service_name}",
{indent}    InstanceId = Guid.NewGuid(),
{indent}    HostName = "test-host",
{indent}    ProcessId = 12345
{indent}  }}{rest}'''

def fix_file(filename):
    with open(filename, 'r') as f:
        content = f.read()

    # Fix pattern: new MessageHop { ServiceName = "X" }
    # This handles single-line declarations
    pattern = r'(\s+)new MessageHop \{ ServiceName = "([^"]+)"( \}|,)'
    content = re.sub(pattern, fix_simple_hop, content)

    # Fix pattern: new MessageHop { ServiceName = "X", ...other props... }
    # More complex multi-property single-line cases
    pattern2 = r'(\s+)new MessageHop \{ ServiceName = "([^"]+)", (Type = [^}]+\})'
    def fix_with_type(match):
        indent = match.group(1)
        service_name = match.group(2)
        rest_props = match.group(3)
        return f'''{indent}new MessageHop {{
{indent}  ServiceInstance = new ServiceInstanceInfo {{
{indent}    ServiceName = "{service_name}",
{indent}    InstanceId = Guid.NewGuid(),
{indent}    HostName = "test-host",
{indent}    ProcessId = 12345
{indent}  }},
{indent}  {rest_props}'''
    content = re.sub(pattern2, fix_with_type, content)

    with open(filename, 'w') as f:
        f.write(content)

if __name__ == '__main__':
    fix_file('tests/Whizbang.Observability.Tests/MessageTracingTests.cs')
    print("Fixed MessageHop instantiations")
