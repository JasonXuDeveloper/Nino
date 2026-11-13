---
name: audit-ci
description: "Audit and change Nino's GitHub Actions (ci.yml, runtimes.yml, unity.yml, fuzz.yml, codeql.yml, release.yml, report.yml, agents.yml, ab-bench.yml and the test branch's A/B workflows) - as fast and parallel as possible, green on main and the test branch, every action pinned to a SHA, path filters that run what a change touches, one Unity license seat, secrets only through Trusted Publishing or repository secrets. Use when changing a workflow, when CI is slow or red, or before a release. Triggers: .github/workflows, .github/actions, actionlint, CI, workflow, matrix, shards, path filter, failed run, slow CI, Unity license, unity.yml, fuzz.yml, ci.yml, dependabot, 工作流, 持续集成, CI 红了, CI 失败, CI 慢, 并行."
metadata:
  internal: true
---

# CI audit

The owner reads every failure email: CI stays green, and a red run is fixed at once.

## Rules

- **Fast and parallel**: jobs that do not depend on each other run side by side; long suites are sharded (the unit
  tests' three shards per configuration, `.github/scripts/test_shards.py` checks they ran everything; `report.yml`'s
  ten shards per architecture, balanced by each shard's "Run benchmarks" minutes in the last report, not by case
  counts: rc.8's shard b took 65 minutes against 35 to 45, its million-object ScatteredTest cases over a minute each;
  a class moves by `BenchmarkCategory`, so each part keeps its baseline); builds are cached where the cache is safe; each job installs only what it needs.
- **Path filters** run a workflow only for the paths its jobs read (ci.yml lists them with a YAML anchor shared by
  push and pull_request); a new folder a job reads is added to its filter. A job whose inputs are committed files
  that another workflow checks runs on those files, not on what produces them: runtimes.yml runs on `runtimes/` and
  `tests/conformance/corpus/` only, because ci.yml's integrations job checks that the corpus and nino-gen's generated files
  are what .NET writes today (its 16 jobs had run for every Nino.Core change). Inside a workflow, jobs of different
  areas are gated by `.github/scripts/changed_paths.py` (a `changes` job diffing against the push's `before` or the
  pull request's base; everything runs when that is unknown, on a schedule and when the workflow changes):
  runtimes.yml per runtime (Miri only for the crate's library code), codeql.yml per language (every language
  weekly). A folder another workflow owns is excluded with `!`, and a file a job links from it is re-included on
  the line after (later patterns win): ci.yml leaves out `src/Nino.Unity/**` (unity.yml's; a Burst or Unity test
  edit had started ci.yml's 16 jobs) but takes `Assets/Test/Runtime/BclFixture*.cs`, which Nino.RuntimeTests links,
  and the package's `Runtime/*.dll`, which `UnityPackageDllTests` reads.
- **Green**: never push a change whose workflow was not run locally in its parts (`verify`); `actionlint` (1.7.12 or newer: 1.7.7 and 1.7.8
  reject the YAML anchors of the path filters with "must be sequence node but got alias node"; `go install
  github.com/rhysd/actionlint/cmd/actionlint@latest`) with `.github/actionlint.yaml` after any workflow edit; a step that pipes a command keeps its failure (`bash -eo
  pipefail` by default). Flaky is not a root cause: find it. A step that downloads has a bound: Ubuntu packages go
  through `.github/actions/apt-install` (a time limit per try, three tries, the .deb files cached), never a bare
  `apt-get`.
- **Automatic NuGet dependency submission** is GitHub-managed. If restore succeeds but
  `component-detection` fails immediately with a shell syntax error, inspect the pinned action
  and rerun just that job before changing repository workflows. The action downloads the latest
  scanner and writes the HTTP response as an executable without checking its status. Run
  42566229349 failed to launch the scanner and passed on attempt 2 at the same commit.
- **Scripts only a release runs are checked on push.** `benchmarks/*.py` and `.github/scripts/*.py` run in
  `report.yml` and `release.yml`, so a broken one showed only at a release: agents.yml's job (path filter on
  `benchmarks/**` and `.github/scripts/**`) runs `python3 -m py_compile` over both folders and
  `python3 -m unittest discover -s benchmarks -p 'test_*.py'` (under a second). Give a new release-time script a
  `test_*.py` in `benchmarks/` on its fixtures, and a script in a new folder a line in that step and its path filter.
  Do not discover in `.github/scripts`: it has no tests (`test_shards.py` is ci.yml's shard checker), and unittest
  exits 5 on "NO TESTS RAN" since Python 3.12. The same holds for checks: whatever report.yml runs before measuring
  runs on every push, on each architecture report.yml measures. `Nino.Benchmark --verify` had run only in ci.yml's
  pull-request job (main lands without pull requests), and v4.0.0-rc.8's failed in all ten Arm64 report shards (a
  probe called x86's CpuId, run 41853674050); it now runs in the integrations job (x64, the Debug build, 15 s) and the
  Arm64 runtime internals job. `DOTNET_EnableHWIntrinsic=0` on x64 is no stand-in for Arm64 (`platforms`).
- **Pinned**: every action by commit SHA with its version in a comment (`uses: actions/checkout@<sha> # v7.0.1`);
  Dependabot updates both, grouped. Give every folder that holds a `uses:` a Dependabot entry: `directory: "/"`
  covers only `.github/workflows`, so the composite actions in `.github/actions` kept setup-dotnet v6.0.0 while
  Dependabot moved the workflows to v6.1.0 (rc.9). After a Dependabot pull request, `git grep 'owner/action@'`
  `.github` shows one SHA per action.
- **Least privilege**: `permissions: contents: read` unless a job writes; no ref taken from an input runs code in
  a cache scope that is not its own (cache poisoning): a job that checks out an input's ref restores caches with
  `actions/cache/restore` and saves them with `actions/cache/save` only when it runs its own commit, validates the
  ref's form first, and names the input for what it holds (`tag`, not `ref`: CodeQL's
  actions/cache-poisoning/direct-cache counts a checkout `ref` from an input named like `*ref*`, `*head*` or
  `*branch*` as untrusted; unity.yml's `ref` input raised code scanning alert #7); publishing goes through Trusted Publishing (NuGet, crates.io,
  npm), never a stored key in a file.
- **Unity**: `unity.yml` runs one Unity version at a time (`max-parallel: 1`) and is never cancelled while running, so
  CI holds at most one license seat; `report.yml`'s Unity section the same. Its matrix has one row per Burst major
  the package supports (2022.3.62f2 for Burst 1.8, 6000.6.9f1 for the editor's built-in Burst 2.0; the owner chose
  two rows over keeping 6000.3), since Burst versions compile the kernels differently (`platforms`,
  references/mono-unity.md); a new Burst major gets a row first. `unity.yml` also builds a WebGL player per
  version after the tests (`webgl` job, `max-parallel: 1`) and runs it in headless Chrome (playwright-core with the
  runner's Chrome, `.github/scripts/webgl_run.mjs`, which must exit as soon as the player reports done: a pending
  `setTimeout` kept Node, and with it the run that holds the `unity-license` group, for 40 more minutes). An agent
  session's GitHub integration cannot `workflow_dispatch` (403 "Resource not accessible by integration", also after
  `add_repo` with push access; the response's `X-Accepted-GitHub-Permissions: actions=write` names the token's missing
  permission, not the workflow: ab-bench.yml is active with `workflow_dispatch` and no environment, round 124): to run unity.yml on an experiment, push a branch whose own unity.yml adds it to
  `on.push.branches`, check first that no Unity run is queued (a newer pending run in the `unity-license` group replaces
  an older pending one: a Dependabot pull request's run, whose jobs all skip, cancelled report.yml's waiting Unity
  call for rc.9, so runs that cannot take the license get a group of their own; push nothing that triggers unity.yml
  between a release and its report's Unity jobs starting), and name the branch in the report: the session cannot delete it either (`git push --delete`
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
  job's own log prints the editor's log only after the editor exits. A bare `Killed` before that log and
  `Unexpected exit code 137` mean the editor alone got SIGKILL (xvfb-run's dash prints the bare word; the timeout
  sends SIGTERM to the whole group first, and test.sh's bash would print `line N: PID Killed`), so the wrapper is not
  the cause. Find the sender before blaming memory, `/dev/shm` or a flag: the container is gone once the step ends,
  so trace the host kernel from a step before the launch (tracefs `signal:signal_generate` filtered to `sig == 9`,
  with `record-tgid`, `record-cmd` and a stack trigger; a second instance recording `sched_process_fork`,
  `sched_process_exec`, `task_rename` and `sched_process_exit` maps each thread to its process and to its PID
  inside its container, counted in creation order from runc's `[1:CHILD]`), print it from an `if: always()` step
  with `dmesg` (an OOM kill shows as `Memory cgroup out of memory`), and map every target to its container PID: a
  fixed container PID across containers means a PID stored by an earlier container, since each container hands out
  PIDs in the same order (unity.yml had these steps from 0a6542e3 and c904dcaf until the cause was found). The
  6000.6.9f1 editor killed itself that way: about 2 s after launch, right after `Registered in ... seconds.` in its
  first domain reload, a thread of `/opt/unity/Editor/Unity` called `kill(2)` with SIGKILL (`code=0`) on the PID
  Burst 2.0's compiler stored in `Library/burst.pid`, taking it for a lingering compiler; the Library cache brought
  the file back, so the PID was a thread of the editor itself (exit 137) or of its `Unity.DataStore` (runs
  43324567209, 43349362911; run 43356711660 printed the file: 296 from the cache, then 287, the PID of every kill).
  `Library/ilpp.pid`, the first suspect (decompiled `KillLingeringILPPRunner`), held other PIDs. Ruled out with the
  kills going on: memory (nothing in `dmesg`, 14 GB available), `/dev/shm` (2 GiB as at the CLI's 1025m),
  code coverage and the Data Store's state. unity.yml's `Delete Unity's stored process IDs` runs before every
  launch: it prints `Library/burst.pid`, `Library/ilpp.pid`, `Library/MSBuild/buildhost.pid` and
  `Library/EditorInstance.json` and deletes them and `Temp`, keeping the compiled scripts and imported assets for
  the cache; with it no editor killed itself (run 43361909823, all three launches of 6000.6 green at the first
  attempt). The owner's call: only that step stays, no retry and no trace (a retry with a kernel-trace judge ran
  from 0a8cfb6e to 7c6f6a93). A listing in a step must never stop the step before its action: 553debd6's
  `find | head` under `pipefail` died of SIGPIPE (exit 141) before its `rm`. A passing launch's editor SIGKILLs
  its `Unity.DataStore` at shutdown, and bash SIGKILLs `UnityAutoQuitte` (`code=128`) at the end of each launch:
  both are the usual end, not a bug. Burst 2.0 ships only inside 6000.6 (the registry's `com.unity.burst` stops at
  1.8.31), so the row cannot move to an older 6000.x. Name no variable of your own `GAME_CI_*`: the CLI reads
  each as an option (yargs `.env("GAME_CI")` under `strict`) and rejects one it does not know.
- **Releases and reports**: `release.yml` and `report.yml` run only on the owner's action or a release; `report.yml`
  runs only the sections the release's tree has, and skips one whose asset the release already has
  (`release_assets.py has`: the manifest, or the old body's heading). Its publish jobs (publish, publish-runtimes,
  publish-generator, publish-unity) run one after another, since each rewrites the manifest and the body, and each
  checks out this commit with credentials: `release_body.py publish` pushes its charts to the release-notes branch
  (`benchmark`, Adding a case). A job that checks out the release's tag (report.yml's bench jobs,
  `unity.yml` called with `tag`, ab-bench.yml's `head/`) runs this commit's workflow on that tag's files: it uses
  actions and scripts by their pinned SHA, never `./.github/actions/...` or a script the tag may lack (tags up to
  v4.0.0-rc.7 have no `.github/actions/setup-dotnet`); steps such a workflow repeats across its jobs share one YAML
  anchor instead (`unity.yml`'s `&setup-dotnet` and `&build-dlls`, with the job's `NINO_CONFIG`).
- **The signed Unity package**: release.yml's `unity` job calls `unity-sign.yml`, which signs the package with
  Unity's UPM CLI (pinned version; secrets `UPM_SERVICE_ACCOUNT_KEY_ID`, `UPM_SERVICE_ACCOUNT_KEY_SECRET`,
  `UPM_ORG_ID`: a Unity Cloud service account with the organization-level role "Package Manager Package Signer")
  and attaches `com.jasonxudeveloper.nino-<version>.tgz` to the Unity tag's release; OpenUPM tracks Nino with
  `trackingMode: githubRelease` and publishes only that asset, so a release whose `unity` job failed is missing on
  OpenUPM until it is re-run. A pre-release's `vX.Y.Z-preview.N` release exists only for that asset: report.yml
  skips it, and every "newest v4 release" lookup in report.yml leaves `-preview.` tags out (rc.10's README steps
  saw preview.210, published after rc.10, as the newest release and skipped the README; 4983c151).
- **Find a downloaded artifact's files, never assume its folder**: `actions/download-artifact` puts the one artifact
  a `pattern` matches either in a folder of its name or straight in `path`, by version; locate a file with `find`
  under `path`. rc.10's build section was produced but report.yml looked in `path/generator-section/`, so the
  release got no build-x64.md and README no build row (6bc1b819). Check a change to the signing step, or new credentials, by pushing `unity-sign.yml` to the test branch
  (it signs and keeps an artifact, no release touched). "User does not have permission to sign package" means
  the service account's role or organization is wrong, not the secrets' presence (run 43286997093; 43287841461
  signed once the owner fixed the role).
- **Concurrency budget**: every matrix that can grow with a request or a file has `max-parallel` (`runtime-ab.yml`
  10, `dotnet-ab.yml` 8, `report.yml`'s bench 12, `ab-bench.yml` and ci.yml's pull-request benchmark 6 (shards.json),
  `unity.yml` 1), and request caps keep a run's job count bounded (8 runtime lines, 3 .NET lines). A push to main
  should start well under the account's 40 concurrent jobs: short checks of one runner share a job, pushes fuzz with
  one libFuzzer runner (three at night), and a job runs only for the area that changed (Path filters).
  How workers share the budget: `benchmark`, "Spend CI as a budget".
- **A/B workflows** live on the test branch: `runtime-ab.yml` reads `.github/ab-request.txt` (Go, Rust,
  TypeScript and generator lines), `dotnet-ab.yml` reads `.github/dotnet-ab-request.txt` (.NET lines); how to write a line is
  `benchmark`. `ab-bench.yml` on main compares a ref with the previous release or its base. An A/B workflow never
  reports a comparison it did not make: a side that cannot be built or measured fails its job with an `::error::`,
  and the summary shows head against base only when every shard that measured head measured base (`ab.py` with one
  report prints head alone, never head against itself). Why: ab-bench fell back to head alone when v4.0.0-rc.7 did not
  build head's benchmarks and printed `ab.py head.md head.md`, a table of 1.00x that read as a comparison (round 124).

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
