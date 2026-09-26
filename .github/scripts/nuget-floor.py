#!/usr/bin/env python3
"""Refuse a version that would sort below one already on nuget.org.

Usage: nuget-floor.py <version>

Exit 0 when <version> is at or above the highest version nuget.org holds for any package in
.github/nuget-packages.txt (equal passes: a re-run of a run that already published). Exit 1, naming
the offending package and version, when it is lower. Exit 2 when nuget.org cannot be read: the caller
must not treat an unreadable feed as a pass.

Unlisted versions count: they still exist, can still be restored by exact version, and a push of the
same number would collide. SemVer 2.0 precedence, prerelease labels compared case-insensitively as
nuget.org does. Why this exists: an abandoned release cut rewinds develop's computed version below the
alphas it published while the cut was open, and nothing else notices (#872; docs/RELEASING.md,
Recovery).
"""
import json
import os
import sys
import urllib.error
import urllib.request


def key(version):
    """Sort key giving SemVer 2.0 precedence (build metadata ignored)."""
    core, _, pre = version.split('+', 1)[0].partition('-')
    nums = tuple(int(x) for x in core.split('.'))
    nums += (0,) * (3 - len(nums))
    if not pre:
        return (nums, 1, ())  # a release sorts above every prerelease of the same core
    ids = []
    for part in pre.split('.'):
        # numeric identifiers sort numerically and below alphanumeric ones
        ids.append((0, int(part), '') if part.isdigit() else (1, 0, part.lower()))
    return (nums, 0, tuple(ids))


def main():
    candidate = sys.argv[1]
    listfile = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'nuget-packages.txt')
    ids = [l.strip() for l in open(listfile) if l.strip() and not l.lstrip().startswith('#')]
    highest, owner = None, None
    for pid in ids:
        url = f'https://api.nuget.org/v3-flatcontainer/{pid.lower()}/index.json'
        try:
            with urllib.request.urlopen(url, timeout=30) as r:
                versions = json.load(r)['versions']
        except urllib.error.HTTPError as e:
            if e.code == 404:
                continue  # never published: nothing to sort below
            print(f'::error::Cannot read nuget.org for {pid}: HTTP {e.code}')
            return 2
        except Exception as e:  # network, timeout, bad JSON
            print(f'::error::Cannot read nuget.org for {pid}: {e}')
            return 2
        for v in versions:
            if highest is None or key(v) > key(highest):
                highest, owner = v, pid
    if highest is None:
        print('No package in the list has been published yet.')
        return 0
    if key(candidate) < key(highest):
        print(f'::error::{candidate} sorts BELOW {highest}, already on nuget.org for {owner}. '
              'Publishing it would hide it from latest-version resolution. Usual cause: a release cut '
              'was abandoned after develop published in its band (#872). Recovery (docs/RELEASING.md, '
              f'Recovery): tag the commit that published {highest} as v{highest} so develop computes '
              'above it, then re-run this run.')
        return 1
    print(f'{candidate} is at or above the highest version on nuget.org ({highest}, {owner}).')
    return 0


if __name__ == '__main__':
    sys.exit(main())
