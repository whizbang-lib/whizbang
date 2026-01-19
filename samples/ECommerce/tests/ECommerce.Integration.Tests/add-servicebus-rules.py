#!/usr/bin/env python3
"""
Adds $Default TrueFilter rules to Azure Service Bus Emulator subscriptions.
Reads Config.json from stdin, adds rules to all subscriptions, writes to stdout.
"""
import json
import sys

def add_true_filter_rule():
    """Creates a default TrueFilter rule that accepts all messages."""
    return {
        "Name": "$Default",
        "Properties": {
            "FilterType": 0,  # SqlFilter
            "SqlFilter": {
                "SqlExpression": "1=1"  # TrueFilter - matches all messages
            }
        }
    }

def add_rules_to_subscriptions(config):
    """Adds $Default TrueFilter rules to all subscriptions in the config."""
    if "UserConfig" not in config or "Namespaces" not in config["UserConfig"]:
        print("ERROR: Invalid config structure", file=sys.stderr)
        return False

    namespaces = config["UserConfig"]["Namespaces"]
    for namespace in namespaces:
        if "Topics" not in namespace:
            continue

        for topic in namespace["Topics"]:
            if "Subscriptions" not in topic:
                continue

            for subscription in topic["Subscriptions"]:
                # Check if subscription already has rules
                if "Rules" not in subscription or not subscription["Rules"]:
                    # Add $Default TrueFilter rule
                    subscription["Rules"] = [add_true_filter_rule()]
                    print(f"Added $Default rule to {topic['Name']}/{subscription['Name']}", file=sys.stderr)
                else:
                    print(f"Subscription {topic['Name']}/{subscription['Name']} already has rules, skipping", file=sys.stderr)

    return True

def main():
    try:
        # Read JSON from stdin
        config = json.load(sys.stdin)

        # Add rules to all subscriptions
        if not add_rules_to_subscriptions(config):
            sys.exit(1)

        # Write modified JSON to stdout
        json.dump(config, sys.stdout, indent=2)

    except json.JSONDecodeError as e:
        print(f"ERROR: Failed to parse JSON: {e}", file=sys.stderr)
        sys.exit(1)
    except Exception as e:
        print(f"ERROR: {e}", file=sys.stderr)
        sys.exit(1)

if __name__ == "__main__":
    main()
