---
name: audit-ci
description: "Audit and change Nino's GitHub Actions (ci.yml, runtimes.yml, unity.yml, fuzz.yml, codeql.yml, release.yml, report.yml, ab-bench.yml and the test branch's A/B workflows) - as fast and parallel as possible, green on main and the test branch, every action pinned to a SHA, path filters that run what a change touches, one Unity license seat, secrets only through Trusted Publishing or repository secrets. Use when changing a workflow, when CI is slow or red, or before a release. Triggers: .github/workflows, .github/actions, actionlint, CI, runner, matrix, shards, cache, path filter, failed run, slow CI, Unity license, dependabot, 工作流, 持续集成, CI 失败, CI 慢, 并行."
metadata:
  internal: true
---

# CI audit

The owner reads every failure email: CI stays green, and a red run is fixed at once.

## Rules

- **Fast and parallel**: jobs that do not depend on each other run side by side; long suites are sharded (the unit
  tests' three shards per configuration, `.github/scripts/test_shards.py` checks they ran everything; `report.yml`'s
  ten shards per architecture); builds are cached where the cache is safe; each job installs only what it needs.
- **Path filters** run a workflow only for the paths it builds or tests (ci.yml lists them with a YAML anchor shared
  by push and pull_request); a new folder a job reads is added to its filter.
- **Green**: never push a change whose workflow was not run locally in its parts (`verify`); `actionlint` with
  `.github/actionlint.yaml` after any workflow edit; a step that pipes a command keeps its failure (`bash -eo
  pipefail` by default). Flaky is not a root cause: find it. A step that downloads has a bound: Ubuntu packages go
  through `.github/actions/apt-install` (a time limit per try, three tries, the .deb files cached), never a bare
  `apt-get`.
- **Pinned**: every action by commit SHA with its version in a comment (`uses: actions/checkout@<sha> # v7.0.1`);
  Dependabot updates both, grouped.
- **Least privilege**: `permissions: contents: read` unless a job writes; no ref taken from an input runs code in
  a cache scope that is not its own (cache poisoning); publishing goes through Trusted Publishing (NuGet, crates.io,
  npm), never a stored key in a file.
- **Unity**: `unity.yml` runs one Unity version at a time (`max-parallel: 1`) and is never cancelled while running, so
  CI holds at most one license seat; `report.yml`'s Unity section the same. `unity.yml` also builds a WebGL player per
  version after the tests (`webgl` job, `max-parallel: 1`) and runs it in headless Chrome (playwright-core with the
  runner's Chrome, `.github/scripts/webgl_run.mjs`, which must exit as soon as the player reports done: a pending
  `setTimeout` kept Node, and with it the run that holds the `unity-license` group, for 40 more minutes). An agent
  session's GitHub integration cannot `workflow_dispatch` (403 "Resource not accessible by integration", also after
  `add_repo` with push access): to run unity.yml on an experiment, push a branch whose own unity.yml adds it to
  `on.push.branches`, check first that no Unity run is queued (a newer pending run in the `unity-license` group replaces
  an older pending one), and name the branch in the report: the session cannot delete it either (`git push --delete`
  returned 403).
- **Releases and reports**: `release.yml` and `report.yml` run only on the owner's action or a release; `report.yml`
  runs only the sections the release's tree has.
- **Concurrency budget**: every matrix that can grow with a request has `max-parallel` (`runtime-ab.yml` 10,
  `dotnet-ab.yml` 8, `unity.yml` 1), and request caps keep a run's job count bounded (8 runtime lines, 3 .NET lines).
  How workers share the budget: `benchmark`, "Spend CI as a budget".
- **A/B workflows** (`runtime-ab.yml`, `dotnet-ab.yml`) live on the test branch and read `.github/ab-request.txt`
  (`benchmark`); `ab-bench.yml` on main compares a ref with the previous release or its base.

## Look for

- Jobs that wait on others without needing their output; steps repeated across jobs that a composite action
  (`.github/actions/setup-dotnet`) or a script would share; caches that never hit; matrix entries that test the same
  thing twice; jobs that run on paths they do not read.
- The slowest job of each workflow (`gh run view --json jobs` or the API): what dominates it, and whether it can be
  sharded or cached.
- Unpinned actions, missing `permissions`, `pull_request_target` or inputs flowing into `run:` unquoted.

- The code standard (`audit-code`: DRY, decoupled, clear) applies to these files, and redundancy in them
  (duplicated logic, dead code, two ways to do one job) is part of this audit; the sweep across layers is
  `audit-redundancy`.

## Proof

`actionlint`, the job's commands run locally, then the pushed run watched to the end; for a speed change, the
before and after durations of the same workflow.
