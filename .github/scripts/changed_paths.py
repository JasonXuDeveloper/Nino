#!/usr/bin/env python3
"""Which groups of files a push or pull request changed, for jobs that run only when theirs did.

    python3 .github/scripts/changed_paths.py [--all-if REGEX] BASE NAME=REGEX ...

Run in a checkout of the commit under test. BASE is the commit to compare it with (a push's github.event.before, a
pull request's base sha); the script fetches it (a shallow fetch is enough: only the two trees are compared). Prints
one line per group, NAME=true when a changed path matches its regular expression (re.search), else NAME=false, for
$GITHUB_OUTPUT. Every group is true when BASE is empty or all zeros (a new branch, a schedule, a manual run), when it
cannot be fetched (a force push that dropped it), or when a changed path matches --all-if (the workflow itself): when
in doubt, everything runs.
"""

import argparse
import re
import subprocess
import sys


def changed(base):
    """The paths changed between BASE and HEAD, or None when they cannot be known."""
    if not base or set(base) == {"0"}:
        return None
    have = subprocess.run(["git", "cat-file", "-e", f"{base}^{{commit}}"], capture_output=True)
    fetch = have if have.returncode == 0 else subprocess.run(
        ["git", "fetch", "-q", "--depth=1", "origin", base], capture_output=True, text=True)
    if fetch.returncode != 0:
        print(f"::notice::{base} could not be fetched ({fetch.stderr.strip()}): every group runs", file=sys.stderr)
        return None
    diff = subprocess.run(["git", "diff", "--name-only", base, "HEAD"], capture_output=True, text=True, check=True)
    return diff.stdout.splitlines()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--all-if", help="a changed path matching this runs every group")
    parser.add_argument("base")
    parser.add_argument("groups", nargs="+", metavar="NAME=REGEX")
    args = parser.parse_args()
    groups = dict(group.split("=", 1) for group in args.groups)
    paths = changed(args.base)
    if paths is not None:
        print(f"{len(paths)} paths changed", file=sys.stderr)
        if args.all_if and any(re.search(args.all_if, path) for path in paths):
            paths = None
    for name, pattern in groups.items():
        hit = paths is None or any(re.search(pattern, path) for path in paths)
        print(f"{name}={'true' if hit else 'false'}")


if __name__ == "__main__":
    main()
