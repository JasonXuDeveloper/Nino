#!/usr/bin/env python3
"""Which groups of files a push or pull request changed, for jobs that run only when theirs did.

    python3 .github/scripts/changed_paths.py [--all-if REGEX] [--tested-in-pr WORKFLOW] BASE NAME=REGEX ...

Run in a checkout of the commit under test. BASE is the commit to compare it with (a push's github.event.before, a
pull request's base sha); the script fetches it (a shallow fetch is enough: only the two trees are compared). Prints
one line per group, NAME=true when a changed path matches its regular expression (re.search), else NAME=false, for
$GITHUB_OUTPUT. Every group is true when BASE is empty or all zeros (a new branch, a schedule, a manual run), when it
cannot be fetched (a force push that dropped it), or when a changed path matches --all-if (the workflow itself): when
in doubt, everything runs.

--tested-in-pr WORKFLOW (a push to main): when HEAD is the merge commit of a pull request whose branch already held
its first parent (it was up to date with main), the merge's tree is the tree that pull request's last run of
WORKFLOW (its file name, e.g. ci.yml) tested, so a successful run of it on that head makes this run a repetition:
every group is false and tested_in=<that run's URL> is printed (tested_in= otherwise). Why the trees are equal: the
pull request's run tested the head merged into main as it was then, an ancestor of the first parent (main moves only
forward), which the head contains. Reads the GitHub API with GITHUB_TOKEN (contents: read, actions: read); any error
leaves every group as the diff says.
"""

import argparse
import json
import os
import re
import subprocess
import sys
import urllib.parse
import urllib.request


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


def pull_request_head(parents, behind_by):
    """The head of the pull request a commit with these parents merged, when the merge's tree is that head's: a merge
    commit (two parents) whose second parent is not behind its first (behind_by of the compare first...second)."""
    if len(parents) != 2 or behind_by != 0:
        return None
    return parents[1]


def api(path):
    """GET a path of this repository's REST API (GITHUB_REPOSITORY, GITHUB_TOKEN) as JSON."""
    url = f"{os.environ.get('GITHUB_API_URL', 'https://api.github.com')}/repos/{os.environ['GITHUB_REPOSITORY']}{path}"
    request = urllib.request.Request(url, headers={
        "Accept": "application/vnd.github+json",
        "Authorization": f"Bearer {os.environ['GITHUB_TOKEN']}",
        "X-GitHub-Api-Version": "2022-11-28",
    })
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def tested_in_pr(workflow):
    """The URL of a successful pull_request run of WORKFLOW that tested HEAD's tree, or None."""
    try:
        head = subprocess.run(["git", "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
        parents = [parent["sha"] for parent in api(f"/commits/{head}")["parents"]]
        behind_by = api(f"/compare/{parents[0]}...{parents[1]}")["behind_by"] if len(parents) == 2 else None
        tip = pull_request_head(parents, behind_by)
        if tip is None:
            print(f"::notice::{head} is not the merge of an up-to-date pull request: the groups run as changed",
                  file=sys.stderr)
            return None
        query = urllib.parse.urlencode({"event": "pull_request", "head_sha": tip, "status": "success", "per_page": 1})
        runs = api(f"/actions/workflows/{urllib.parse.quote(workflow)}/runs?{query}")["workflow_runs"]
        if not runs:
            print(f"::notice::{workflow} has no successful pull request run on {tip}: the groups run as changed",
                  file=sys.stderr)
            return None
        return runs[0]["html_url"]
    except Exception as error:  # noqa: BLE001 - any failure means "not known to be tested": run
        print(f"::notice::could not tell whether a pull request tested this tree ({error}): the groups run as changed",
              file=sys.stderr)
        return None


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--all-if", help="a changed path matching this runs every group")
    parser.add_argument("--tested-in-pr", metavar="WORKFLOW",
                        help="no group runs when a pull request's run of this workflow tested HEAD's tree")
    parser.add_argument("base")
    parser.add_argument("groups", nargs="+", metavar="NAME=REGEX")
    args = parser.parse_args()
    groups = dict(group.split("=", 1) for group in args.groups)
    tested = tested_in_pr(args.tested_in_pr) if args.tested_in_pr else None
    if tested:
        print(f"::notice::{args.tested_in_pr} passed on this tree in its pull request ({tested}): nothing runs again",
              file=sys.stderr)
    paths = changed(args.base) if not tested else []
    if paths is not None:
        print(f"{len(paths)} paths changed", file=sys.stderr)
        if args.all_if and any(re.search(args.all_if, path) for path in paths):
            paths = None
    for name, pattern in groups.items():
        hit = paths is None or any(re.search(pattern, path) for path in paths)
        print(f"{name}={'true' if hit else 'false'}")
    if args.tested_in_pr:
        print(f"tested_in={tested or ''}")


if __name__ == "__main__":
    main()
