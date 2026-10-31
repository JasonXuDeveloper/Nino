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
- **Path filters** run a workflow only for the paths its jobs read (ci.yml lists them with a YAML anchor shared by
  push and pull_request); a new folder a job reads is added to its filter. A job whose inputs are committed files
  that another workflow checks runs on those files, not on what produces them: runtimes.yml runs on `runtimes/` and
  `conformance/corpus/` only, because ci.yml's integrations job checks that the corpus and nino-gen's generated files
  are what .NET writes today (its 16 jobs had run for every Nino.Core change). Inside a workflow, jobs of different
  areas are gated by `.github/scripts/changed_paths.py` (a `changes` job diffing against the push's `before` or the
  pull request's base; everything runs when that is unknown, on a schedule and when the workflow changes):
  runtimes.yml per runtime (Miri only for the crate's library code), codeql.yml per language (every language
  weekly).
- **Green**: never push a change whose workflow was not run locally in its parts (`verify`); `actionlint` (1.7.12 or newer: 1.7.7 and 1.7.8
  reject the YAML anchors of the path filters with "must be sequence node but got alias node"; `go install
  github.com/rhysd/actionlint/cmd/actionlint@latest`) with `.github/actionlint.yaml` after any workflow edit; a step that pipes a command keeps its failure (`bash -eo
  pipefail` by default). Flaky is not a root cause: find it. A step that downloads has a bound: Ubuntu packages go
  through `.github/actions/apt-install` (a time limit per try, three tries, the .deb files cached), never a bare
  `apt-get`.
- **Scripts only a release runs are checked on push.** `benchmarks/*.py` and `.github/scripts/*.py` run in
  `report.yml` and `release.yml`, so a broken one showed only at a release: agents.yml's job (path filter on
  `benchmarks/**` and `.github/scripts/**`) runs `python3 -m py_compile` over both folders and
  `python3 -m unittest discover -s benchmarks -p 'test_*.py'` (under a second). Give a new release-time script a
  `test_*.py` in `benchmarks/` on its fixtures, and a script in a new folder a line in that step and its path filter.
  Do not discover in `.github/scripts`: it has no tests (`test_shards.py` is ci.yml's shard checker), and unittest
  exits 5 on "NO TESTS RAN" since Python 3.12.
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
  returned 403). Pin `cliVersion` on every game-ci action that runs the game-ci CLI (unity-test-runner 4.4+,
  unity-builder 6+; all to one release): its default `latest` asks the GitHub API on every run, and a 403 from the rate
  limit failed a job. Dependabot bumps the action, never that input: on a game-ci major, read its `action.yml` at the
  new SHA for inputs it ignores (unity-builder 6.0.0's `action.yml` calls `unityVersion` ignored, yet it reaches the
  CLI as `--engineVersion`) and keep the editor chosen by `customImage`; prove it from the job's log
  (`Initialize engine version: 6000.3.27f1` in the 6000.3 WebGL job of 4db6bcc4). Compare the pin with the CLI's newest
  tag (`git ls-remote --tags https://github.com/game-ci/cli`; v0.1.72 was the newest in round 103). A custom `buildMethod` under unity-builder 6 prints the CLI's build summary
  (a `# Build results #` banner, `Errors: 0`, then a `Size:` line, as `WebGLBenchBuild` does): the CLI parses the log
  for it and failed the WebGL job of a build Unity reported as succeeded (4.8.2 never looked).
- **Bound every Unity launch inside its container**, with `ENGINE_LAUNCH_WRAPPER: timeout -k 60 <N>m` in the env
  of each game-ci step (CLI v0.1.72 prefixes every `unity-editor` call of activation, tests and builds with it, read
  from the action's environment like `UNITY_EMAIL`; the license return is not wrapped), and give each such step a
  `timeout-minutes` above that bound as the backstop. Why: the Unity 2022.3 editor hangs now and then in batch mode
  right after loading the project (its log ends at `Unloading 3 Unused Serialized files`, where a normal launch goes
  on to `Unloading 45 unused Assets` and quits; licensing had connected and resolved, so it waits on nothing
  external), and a hung launch held the seat for the job's 100 minutes and kept the other Unity jobs from running
  (runs 41082628657, editmode launch, and 41172149972, COMBINE_RESULTS launch). `timeout` lets the container's script
  go on and return the license; a cancelled or timed-out step does not (the script is the container's PID 1 and gets
  no signal it handles). Size bounds from the jobs' step durations (`gh api .../runs/<id>/jobs`, steps'
  `started_at`/`completed_at`) at five to ten times the normal maximum. Run the editor's tests as two steps
  (`testMode: playmode`, then `editmode`), never `testMode: all`: `all` adds a third launch, COMBINE_RESULTS (the
  editor with `-quit`, for the Code Coverage package to merge results), and the project has no such package, so it
  did nothing ("Coverage results directory does not exist") but give the editor another chance to hang. To see where
  a hung launch stopped, read `<platform>.log` in the job's uploaded `test-results-unity-<version>` artifact: the
  job's own log prints the editor's log only after the editor exits.
- **Releases and reports**: `release.yml` and `report.yml` run only on the owner's action or a release; `report.yml`
  runs only the sections the release's tree has. A job that checks out the release's tag (report.yml's bench jobs,
  `unity.yml` called with `ref`, ab-bench.yml's `head/`) runs this commit's workflow on that tag's files: it uses
  actions and scripts by their pinned SHA, never `./.github/actions/...` or a script the tag may lack (tags up to
  v4.0.0-rc.7 have no `.github/actions/setup-dotnet`); steps such a workflow repeats across its jobs share one YAML
  anchor instead (`unity.yml`'s `&setup-dotnet` and `&build-dlls`, with the job's `NINO_CONFIG`).
- **Concurrency budget**: every matrix that can grow with a request or a file has `max-parallel` (`runtime-ab.yml`
  10, `dotnet-ab.yml` 8, `report.yml`'s bench 12, `ab-bench.yml` and ci.yml's pull-request benchmark 6 (shards.json),
  `unity.yml` 1), and request caps keep a run's job count bounded (8 runtime lines, 3 .NET lines). A push to main
  should start well under the account's 40 concurrent jobs: short checks of one runner share a job, pushes fuzz with
  one libFuzzer runner (three at night), and a job runs only for the area that changed (Path filters).
  How workers share the budget: `benchmark`, "Spend CI as a budget".
- **A/B workflows** (`runtime-ab.yml`, `dotnet-ab.yml`) live on the test branch and read `.github/ab-request.txt`
  (`benchmark`); `ab-bench.yml` on main compares a ref with the previous release or its base.

## Look for

- Jobs that wait on others without needing their output; steps repeated across jobs that a composite action
  (`.github/actions/setup-dotnet`), a script or a YAML anchor (on a tag's tree, "Releases and reports") would share;
  scripts that run only in `report.yml` or `release.yml` without a test agents.yml runs (Rules, "Scripts only a
  release runs"); caches that never hit; matrix entries that test the same
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
