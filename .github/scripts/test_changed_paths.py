"""Tests of changed_paths.py's rule for a push that repeats a pull request's run (python3 -m unittest)."""

import io
import os
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import changed_paths  # noqa: E402


class PullRequestHead(unittest.TestCase):
    def test_up_to_date_merge(self):
        self.assertEqual(changed_paths.pull_request_head(["main", "head"], 0), "head")

    def test_merge_of_a_branch_behind_main(self):
        # main moved after the branch's last run: the merge's tree was never tested as a whole.
        self.assertIsNone(changed_paths.pull_request_head(["main", "head"], 3))

    def test_not_a_merge(self):
        self.assertIsNone(changed_paths.pull_request_head(["parent"], None))
        self.assertIsNone(changed_paths.pull_request_head(["a", "b", "c"], 0))


class Main(unittest.TestCase):
    def run_main(self, *argv):
        out = io.StringIO()
        with mock.patch.object(sys, "argv", ["changed_paths.py", *argv]), redirect_stdout(out), \
                mock.patch.object(sys, "stderr", io.StringIO()):
            changed_paths.main()
        return dict(line.split("=", 1) for line in out.getvalue().splitlines())

    def test_tested_in_pr_turns_every_group_off(self):
        with mock.patch.object(changed_paths, "tested_in_pr", return_value="https://run"), \
                mock.patch.object(changed_paths, "changed") as changed:
            result = self.run_main("--tested-in-pr", "ci.yml", "base", "a=.", "b=^x/")
        changed.assert_not_called()
        self.assertEqual(result, {"a": "false", "b": "false", "tested_in": "https://run"})

    def test_untested_push_runs_what_changed(self):
        with mock.patch.object(changed_paths, "tested_in_pr", return_value=None), \
                mock.patch.object(changed_paths, "changed", return_value=["src/a.cs"]):
            result = self.run_main("--tested-in-pr", "ci.yml", "base", "src=^src/", "go=^runtimes/go/")
        self.assertEqual(result, {"src": "true", "go": "false", "tested_in": ""})

    def test_without_the_option_no_tested_in_line(self):
        with mock.patch.object(changed_paths, "changed", return_value=None):
            result = self.run_main("base", "a=^src/")
        self.assertEqual(result, {"a": "true"})

    def test_lookahead_groups(self):
        # ci.yml's unit tests run for any change outside the other runtimes and runtimes/csharp/benchmarks/.
        pattern = "unit=^(?!runtimes/(go|rust|typescript)/|runtimes/csharp/benchmarks/)"
        with mock.patch.object(changed_paths, "changed", return_value=["runtimes/go/a.go", "runtimes/csharp/benchmarks/x.cs"]):
            self.assertEqual(self.run_main("base", pattern), {"unit": "false"})
        with mock.patch.object(changed_paths, "changed", return_value=["runtimes/go/a.go", "runtimes/csharp/src/Nino.Core/a.cs"]):
            self.assertEqual(self.run_main("base", pattern), {"unit": "true"})

    def test_api_failure_means_not_tested(self):
        with tempfile.TemporaryDirectory() as repo:
            subprocess.run(["git", "init", "-q", repo], check=True)
            subprocess.run(["git", "-C", repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q",
                            "--allow-empty", "-m", "x"], check=True)
            cwd = os.getcwd()
            os.chdir(repo)
            try:
                with mock.patch.object(changed_paths, "api", side_effect=OSError("offline")), \
                        mock.patch.object(sys, "stderr", io.StringIO()):
                    self.assertIsNone(changed_paths.tested_in_pr("ci.yml"))
            finally:
                os.chdir(cwd)


if __name__ == "__main__":
    unittest.main()
