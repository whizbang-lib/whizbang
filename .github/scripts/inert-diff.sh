#!/usr/bin/env bash
# Usage: inert-diff.sh <base-sha> <head-sha>
# Exit 0 when every file that differs from <base> to <head> is inert (.github/inert-paths.txt);
# exit 1 when any file is code, or when the difference cannot be established (a failed lookup, or a
# diff too large for the compare API to list in full). Prints the verdict and the offending files.
# Reads the GitHub compare API: needs GH_TOKEN and REPO. <base> must be an ancestor of <head> (a
# queue commit, a develop merge or a release cut, measured from the PR head or the commit it came
# from), so the three-dot compare is exactly the change between the two trees.
set -uo pipefail
BASE="$1"; HEAD="$2"
LIST="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/inert-paths.txt"
FILES=$(gh api "repos/$REPO/compare/$BASE...$HEAD" --jq 'if (.files | length) >= 300 then "TOO_MANY" else .files[].filename end' 2>/dev/null) \
  || { echo "code: cannot compare $BASE...$HEAD"; exit 1; }
[ "$FILES" = TOO_MANY ] && { echo "code: 300+ files differ, too many to classify"; exit 1; }
[ -z "$FILES" ] && { echo "inert: no files differ"; exit 0; }
MATCH=$(cat <<'PY'
import re, sys
def rx(glob):  # ** crosses directories, * does not
    out = ''
    i = 0
    while i < len(glob):
        if glob.startswith('**', i): out += '.*'; i += 2
        elif glob[i] == '*': out += '[^/]*'; i += 1
        else: out += re.escape(glob[i]); i += 1
    return re.compile('^' + out + '$')
pats = [rx(l.strip()) for l in open(sys.argv[1]) if l.strip() and not l.lstrip().startswith('#')]
code = [f for f in sys.stdin.read().split('\n') if f and not any(p.match(f) for p in pats)]
if code:
    print('code: ' + ', '.join(code[:20]) + (' ...' if len(code) > 20 else ''))
    sys.exit(1)
print('inert: only docs-only paths differ')
PY
)
printf '%s\n' "$FILES" | python3 -c "$MATCH" "$LIST"
