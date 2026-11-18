---
name: audit-ci
description: "Audit and change Nino's GitHub Actions (ci.yml, runtimes.yml, unity.yml, fuzz.yml, codeql.yml, release.yml, report.yml, agents.yml, triage.yml, ab-bench.yml and the test branch's A/B workflows) - as fast and parallel as possible, green on main and the test branch, every action pinned to a SHA, path filters that run what a change touches, one Unity license seat, secrets only through Trusted Publishing or repository secrets. Use when changing a workflow, when CI is slow or red, or before a release. Triggers: .github/workflows, .github/actions, actionlint, CI, workflow, matrix, shards, path filter, failed run, slow CI, Unity license, unity.yml, fuzz.yml, ci.yml, triage.yml, issue triage, issue forms, dependabot, maple, self-hosted runner, queued jobs, 工作流, 自托管, 排队, 持续集成, CI 红了, CI 失败, CI 慢, 并行."
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
  the line after (later patterns win): ci.yml leaves out `runtimes/csharp/src/Nino.Unity/**` (unity.yml's; a Burst or Unity test
  edit had started ci.yml's 16 jobs) but takes `Assets/Test/Runtime/BclFixture*.cs`, which Nino.RuntimeTests links,
  and the package's `Runtime/*.dll`, which `UnityPackageDllTests` reads. ci.yml's `changes` job splits it further:
  the unit tests skip a change that is only under `runtimes/` or `runtimes/csharp/benchmarks/` (they read neither), runtime
  internals one only under `runtimes/`; the integrations job always runs.
- **A push to main repeats nothing its pull request proved.** `main` moves through merge commits of pull requests
  whose branch had main merged in, so the merge's tree is the one the pull request's last run tested:
  `changed_paths.py --tested-in-pr <workflow>` turns every group off when the merge's first parent is in its second
  (compare API, `behind_by` 0) and that workflow passed on a `pull_request` run of the second, and prints the run.
  ci.yml (its "Build and Test" then passes and names the run, so release.yml's "Require ci.yml to have passed" still
  finds a passing run on main) and fuzz.yml (the nightly run fuzzes main) use it; runtimes.yml runs again (only main
  saves the Rust caches pull requests restore) and codeql.yml too (code scanning shows main's alerts from main's own
  analyses). A merge of a branch that was behind main runs as before.
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
- **Scripts only a release runs are checked on push.** `runtimes/csharp/benchmarks/*.py` and `.github/scripts/*.py` run in
  `report.yml` and `release.yml`, so a broken one showed only at a release: agents.yml's job (path filter on
  `runtimes/csharp/benchmarks/**` and `.github/scripts/**`) runs `python3 -m py_compile` over both folders and
  `python3 -m unittest discover -s benchmarks -p 'test_*.py'` (under a second). Give a new release-time script a
  `test_*.py` in `runtimes/csharp/benchmarks/` on its fixtures, and a script in a new folder a line in that step and its path filter.
  In `.github/scripts` name each test file (`-p 'test_changed_paths.py'`, `-p 'test_layout.py'`) instead of discovering `test_*.py`:
  `test_shards.py` there is ci.yml's shard checker, not a test, and unittest exits 5 on "NO TESTS RAN" since
  Python 3.12. The same holds for checks: whatever report.yml runs before measuring
  runs on every push, on each architecture report.yml measures. `Nino.Benchmark --verify` had run only in ci.yml's
  pull-request job (main landed without pull requests then), and v4.0.0-rc.8's failed in all ten Arm64 report shards (a
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
  (`benchmark-cases`, Adding a case on .NET). A job that checks out the release's tag (report.yml's bench jobs,
  `unity.yml` called with `tag`, ab-bench.yml's `head/`) runs this commit's workflow on that tag's files: it uses
  actions and scripts by their pinned SHA, never `./.github/actions/...` or a script the tag may lack (tags up to
  v4.0.0-rc.7 have no `.github/actions/setup-dotnet`); steps such a workflow repeats across its jobs share one YAML
  anchor instead (`unity.yml`'s `&setup-dotnet` and `&build-dlls`, with the job's `NINO_CONFIG`).
- **A workflow finds every path inside a tree with `.github/scripts/layout.py`, never by naming it**, wherever it
  runs one tree's code with another tree's scripts: report.yml (the tag's projects, this commit's `shards.json` and
  `reports/`), ab-bench.yml (head's benchmark project put under each compared ref's .NET root), and the test branch's
  runtime-ab.yml and dotnet-ab.yml (each side's runtimes, nino-gen, generator and benchmark project). Why: a tag or
  commit may come from either side of a move (rc.7's `src/Nino.Benchmark`, rc.8's `benchmarks/v3`, the .NET projects'
  move into `runtimes/csharp`, #273), and a path typed for one layout fails the other. How: `layout.py --ref <ref>`
  (git ls-tree, no checkout), `layout.py <dir>` (a checkout) or `--list` print `KEY=path` (empty where the tree has
  none; `--prefix`, `--json` for a job output read with `fromJSON(...).KEY`, `--get KEY`, which fails on an empty
  one); `--pathspec KEY` gives every layout's path for `git log --`. report.yml's target job resolves both trees once
  (outputs `layout`, the tag's, and `own`, this commit's); a job checking out only the tag reads them from there. A
  thing that moves gets its candidates in `KEYS`, with a case in `test_layout.py`, which checks rc.7, rc.8, rc.10,
  this commit and a synthetic moved tree (agents.yml fetches the tags).
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
- **maple, the self-hosted runner** (`[self-hosted, Linux, X64, maple]`: Ubuntu, the .NET SDKs runtimes/csharp/global.json and
  the jobs ask for, installed there by hand, rustup, gh, jq, python3; no sudo, so no apt, no Docker of ours, no
  Chrome; about 7.7 GB of RAM, 72 GB of disk with some 44 GB free, no AVX-512 VBMI; a shared host that also runs
  some 13 Docker containers and an rclone mount) runs release.yml, unity-sign.yml, report.yml's jobs that are no
  matrix, and CI's short jobs of trusted runs: ci.yml's plan, "Build and Test" and benchmark plan, fuzz.yml's plan,
  the plans of runtimes.yml and codeql.yml, and agents.yml. A CI job goes there only through the one condition in
  its `runs-on` (a push, `workflow_dispatch`, `schedule`, or a `pull_request` whose head repository is this one and
  whose author is not Dependabot, while the repository variable `NINO_MAPLE` is not `false`), with a GitHub-hosted
  fallback for everything else; `NINO_MAPLE=false` sends CI back to GitHub's runners when maple is offline (a job for
  an absent self-hosted runner waits a day). The condition routes honest runs only: a fork's pull request runs its
  own copy of the workflow and can name maple itself, so the guard is on maple (a job-started hook that refuses a
  pull request from another repository or from Dependabot: `ACTIONS_RUNNER_HOOK_JOB_STARTED`) and the repository's
  approval of fork runs (GitHub: "self-hosted runners should almost never be used for public repositories").
  Keep off it: jobs that install with apt (libFuzzer's clang, Mono, qemu) or run Docker (Unity), the CodeQL analyses
  (a bundle of about 1.5 GB per version), the unit test configurations (three processes of about 3.5 GB each), the
  mutational fuzzers (without VBMI its "SIMD" and "without AVX-512" runs take the same paths, where GitHub's x64
  runners have AVX-512 about half the time, and the nightly two 30-minute configurations would hold a runner for an
  hour beside the other job's memory), the benchmark matrices (published numbers come from GitHub's runners), and anything a fork
  or Dependabot started.
- **Leave nothing on maple after a job.** Every job there starts with "Prepare maple" and ends with "Free maple's
  disk" (each workflow's first maple job defines them as the anchors `&maple-prepare` and `&free-maple`, the others
  use them; inline steps, not a local action, since release.yml and report.yml check out tags that lack one). Prepare
  fails at once below 10 GB free under the work directory (the largest job, release's build and pack with its own
  NuGet packages, needs a few GB; a full disk would show as an odd build error), then points every cache into
  `$RUNNER_TEMP/home`: `NUGET_PACKAGES`, `NUGET_HTTP_CACHE_PATH`, `NUGET_PLUGINS_CACHE_PATH`, `DOTNET_CLI_HOME`,
  `npm_config_cache`, `CARGO_HOME`, `GOMODCACHE`, `GOCACHE` and `TMPDIR`. Free (`if: always()`, after the uploads)
  deletes only what no post step reads: `$RUNNER_TEMP/home` and the checkout's untracked files (`git clean -fdx`).
  Never the checkout itself or the rest of RUNNER_TEMP: post steps run after every step of the job, and the runner
  reads `.github/actions/setup-dotnet/action.yml` again then for the post steps of the actions inside it (run
  43847416342: a Free step that deleted the workspace failed the job with "Can't find 'action.yml' ... under
  .../.github/actions/setup-dotnet"); checkout's post step reads `.git`. maple's job-completed hook, which runs after
  the post steps, deletes the rest; it must hold at least this (`ACTIONS_RUNNER_HOOK_JOB_COMPLETED` in the runner's
  `.env`, the script outside the runner's folder):

  ```bash
  #!/usr/bin/env bash
  # After every job and its post steps: the job's checkouts (_work/<repo>/*) and RUNNER_TEMP (_work/_temp).
  set -u
  repo_dir=$(dirname "${GITHUB_WORKSPACE:?}")
  find "$repo_dir" "${RUNNER_TEMP:?}" -mindepth 1 -maxdepth 1 -exec rm -rf {} + || true
  df -h "$repo_dir" | tail -n 1
  exit 0
  ```

  Its job-started hook (`ACTIONS_RUNNER_HOOK_JOB_STARTED`), the guard against forks the maple bullet names; a
  non-zero exit fails the job before any step:

  ```bash
  #!/usr/bin/env bash
  set -euo pipefail
  case "$GITHUB_EVENT_NAME" in
    push|workflow_dispatch|schedule|release|workflow_call) exit 0 ;;
    pull_request) ;;
    *) echo "maple refuses $GITHUB_EVENT_NAME"; exit 1 ;;
  esac
  head=$(jq -r '.pull_request.head.repo.full_name' "$GITHUB_EVENT_PATH")
  author=$(jq -r '.pull_request.user.login' "$GITHUB_EVENT_PATH")
  [[ "$head" == "$GITHUB_REPOSITORY" && "$author" != 'dependabot[bot]' && "$GITHUB_ACTOR" != 'dependabot[bot]' ]] ||
    { echo "maple refuses a pull request from $head by $author"; exit 1; }
  ```

  No `actions/cache` there (setup-dotnet skips its `cache` input on a self-hosted runner): the job's caches are its
  own and go with it, and a cache action's post step would read paths Free deleted. Only the toolchains stay: the .NET SDKs (`.github/actions/setup-dotnet`
  names a missing one on a self-hosted runner instead of installing into `/usr/share/dotnet`), rustup's toolchains in
  `RUSTUP_HOME`, and the Node.js of the tool cache. Whatever a release needs is fetched per run: the UPM CLI and npm
  into RUNNER_TEMP, credentials as OIDC tokens or repository secrets. A new maple job gets both steps; a step that
  installs something else installs it under RUNNER_TEMP.
- **Two runner processes on maple, light jobs only** (`maple-nino` and `maple-nino-2`). A runner runs one job at a
  time, so two processes run two maple jobs side by side in about 7.7 GB of RAM: light jobs only (plans, checks,
  publishing, the release's build and pack); no heavy one (a unit test configuration, a fuzzer), which would swap
  beside another. More processes need a larger host: each runner is its own folder (a copy of the runner package)
  with its own `./config.sh --url https://github.com/JasonXuDeveloper/Nino --token <registration token> --name
  maple-nino-N --labels maple` and its own service (`sudo ./svc.sh install <user>`, then `sudo ./svc.sh start`), and
  the host runs about one heavy job per 4 cores and 8 GB.
- **The build-time benchmark waits for maple to be quiet, and never fails a release over it.** report.yml's
  `generator` step finds its own `Runner.Worker` (the first ancestor of its shell with that name; each running job
  is one Worker, an idle runner keeps only its `Runner.Listener`), waits until no other Worker runs on the host
  (polling every 15 s, up to 30 minutes), watches for one every 5 s while it measures, and measures once more if one
  started; still not quiet, it publishes with a warning in the step summary. Counting runners refused every release
  once the second runner came; a repository variable such as `NINO_MAPLE` cannot be set from a workflow without an
  admin token, and a concurrency group shared by every maple job would cancel all but one waiting job. The host's own
  containers are the noise left. Tested with fake Workers (a copy of bash named `Runner.Worker`, running each command
  with `; :` so it does not exec and rename itself): a busy start, a job during the first measurement only, and jobs
  during both. A test's own Worker must run the step as a child `bash`, as the runner does: subshells of a renamed
  bash keep its name and read as other jobs.
- **The pull request benchmark is on request**: ci.yml runs `runtimes/csharp/benchmarks/bench-cases.json`'s quick subset
  (`[BenchCase(Quick = true)]`, Nino and MemoryPack, three shards) only for a pull request labeled `benchmark` (when opened, reopened or pushed to; labeling alone starts nothing), in quick mode
  (`NINO_BENCH_QUICK=1`: a trend, never published), and runtimes.yml's `benchmark` job the Go, Rust and TypeScript
  benches of the runtimes the pull request changed, quick too (each bench reads `NINO_BENCH_QUICK`); report.yml at a
  release runs every case with the full protocol (#333; its runtime jobs took 3, 6 and 11 minutes of their 40 in
  rc.10). ab-bench.yml on the branch gives a comparison with main or the last release.
- **Untrusted text never reaches a shell or a self-hosted runner.** `triage.yml` reads issues anyone can open: it
  runs on `ubuntu-latest` (GitHub-hosted), passes only the issue number to its script through `env`, never
  `${{ github.event.issue.title }}` or the body in a `run:`, and holds only `issues: write`, `contents: read` and
  `id-token: write`. Claude is reached by Workload Identity Federation (the job's OIDC token, audience
  `https://api.anthropic.com`, fetched right before the script since it lives about five minutes), never an API key
  secret; the SDK is installed from `.github/scripts/triage/requirements.txt` with `--require-hashes` (regenerate it
  with the command in its header after changing `requirements.in`). A failed model call leaves the deterministic
  labels and milestone applied and the job green (`triage.py`'s `classify`), so a broken credential never fails or
  spams an issue; check the run's log for `triage: model call failed`, and the reason in the Console's Workload
  identity > Authentication events (the run's log says only HTTP 401). Scope the federation rule to one workspace,
  or set `ANTHROPIC_WORKSPACE_ID` in the job: a rule enabled in all workspaces rejects an exchange that names none
  (`workspace_id_required`; #313 and #320 got no model answer until the rule was scoped to Default on 2026-11-16).
  The "Could not fetch the OIDC token" in the log is the step's echoed script; the step failed only if a
  `::warning::` line follows. Suggestions (a duplicate, a split) are comments only on issues from outside the
  maintainers, since a comment emails the author; a maintainer's own go to the run's summary (`wants_comment`).
- **Concurrency budget**: every matrix that can grow with a request or a file has `max-parallel` (`runtime-ab.yml`
  10, `dotnet-ab.yml` 8, `report.yml`'s bench 12, `ab-bench.yml` 6 (shards.json), ci.yml's labeled pull-request benchmark 3 (the quick shards),
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
