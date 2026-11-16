"""Tests of triage.py's deterministic part: the milestone rule, tags, issue forms and the model answer's checks.

python3 -m unittest discover -s .github/scripts/triage (agents.yml runs it on every push to .github/scripts).
"""
import unittest

from triage import (
    COMMENT_MARKER,
    Plan,
    comment_body,
    form_areas,
    form_breaking,
    has_kind_label,
    kind_from_labels,
    latest_version,
    milestone_title,
    neutralize,
    parse_classification,
    parse_tag,
    upsert_comment,
    wants_comment,
)

AREAS = {"area: core", "area: generator", "area: unity", "area: dotnet-11"}


class MilestoneRule(unittest.TestCase):
    # (latest tag, kind, breaking, milestone): every row of the owner's rule.
    ROWS = [
        # Stable X.Y.Z: bug to the next patch, anything else to the next minor, breaking to the next major.
        ("v4.0.0", "bug", False, "v4.0.1"),
        ("v4.0.0", "feature", False, "v4.1.0"),
        ("v4.0.0", "perf", False, "v4.1.0"),
        ("v4.0.0", "docs", False, "v4.1.0"),
        ("v4.0.0", "other", False, "v4.1.0"),
        ("v4.0.0", "feature", True, "v5.0.0"),
        ("v4.0.0", "bug", True, "v5.0.0"),
        ("v4.2.7", "bug", False, "v4.2.8"),
        # X.Y.Z-alpha.N: bug, feature and breaking all stay in X.Y.Z.
        ("v4.1.0-alpha.3", "bug", False, "v4.1.0"),
        ("v4.1.0-alpha.3", "feature", False, "v4.1.0"),
        ("v4.1.0-alpha.3", "perf", False, "v4.1.0"),
        ("v4.1.0-alpha.3", "feature", True, "v4.1.0"),
        # X.Y.Z-beta.N and -rc.N: bug, perf and docs stay, feature and other to the next minor, breaking to the next
        # major.
        ("v4.1.0-beta.2", "bug", False, "v4.1.0"),
        ("v4.1.0-beta.2", "perf", False, "v4.1.0"),
        ("v4.1.0-beta.2", "docs", False, "v4.1.0"),
        ("v4.1.0-beta.2", "feature", False, "v4.2.0"),
        ("v4.1.0-beta.2", "other", False, "v4.2.0"),
        ("v4.1.0-beta.2", "feature", True, "v5.0.0"),
        ("v4.1.0-beta.2", "perf", True, "v5.0.0"),
        ("v4.0.0-rc.10", "bug", False, "v4.0.0"),
        ("v4.0.0-rc.10", "perf", False, "v4.0.0"),
        ("v4.0.0-rc.10", "docs", False, "v4.0.0"),
        ("v4.0.0-rc.10", "feature", False, "v4.1.0"),
        ("v4.0.0-rc.10", "other", False, "v4.1.0"),
        ("v4.0.0-rc.10", "bug", True, "v5.0.0"),
        ("v4.0.0-rc.10", "feature", True, "v5.0.0"),
        # No milestone for a question or an unknown kind.
        ("v4.0.0", "question", False, None),
        ("v4.0.0-rc.10", None, False, None),
        ("v4.1.0-alpha.1", "question", True, None),
    ]

    def test_every_row(self):
        for tag, kind, breaking, expected in self.ROWS:
            with self.subTest(tag=tag, kind=kind, breaking=breaking):
                self.assertEqual(milestone_title(parse_tag(tag), kind, breaking), expected)


class Tags(unittest.TestCase):
    def test_only_release_tags_parse(self):
        for tag in ["v4.0.0-preview.210", "runtimes/go/v4.0.0-rc.10", "v4.0", "4.0.0", "v4.0.0-rc", "v4.0.0-gamma.1"]:
            with self.subTest(tag=tag):
                self.assertIsNone(parse_tag(tag))

    def test_latest_orders_prereleases_numerically_and_below_stable(self):
        self.assertEqual(latest_version(["v4.0.0-rc.9", "v4.0.0-rc.10", "v4.0.0-preview.210", "v3.3.13"]),
                         parse_tag("v4.0.0-rc.10"))
        self.assertEqual(latest_version(["v4.0.0-rc.10", "v4.0.0"]), parse_tag("v4.0.0"))
        self.assertEqual(latest_version(["v4.1.0-alpha.1", "v4.0.0", "v4.1.0-beta.1"]), parse_tag("v4.1.0-beta.1"))
        self.assertIsNone(latest_version(["runtimes/go/v4.0.0"]))


class IssueForms(unittest.TestCase):
    BODY = ("### Area\n\ncore, generator, nonsense\n\n### Breaking change\n\n- [X] It breaks existing code or bytes\n\n"
            "### Description\n\n### Area\nnot a heading the form wrote")

    def test_areas_map_to_existing_labels_only(self):
        self.assertEqual(form_areas(self.BODY, AREAS), ["area: core", "area: generator"])
        self.assertEqual(form_areas("### Area\n\n_No response_\n", AREAS), [])
        self.assertEqual(form_areas("A plain issue without a form.", AREAS), [])

    def test_breaking_checkbox(self):
        self.assertTrue(form_breaking(self.BODY))
        self.assertIs(form_breaking("### Breaking change\n\n- [ ] It breaks existing code or bytes\n"), False)
        self.assertIs(form_breaking("### Breaking change\n\n_No response_\n"), False)
        self.assertIsNone(form_breaking("- [x] a checklist elsewhere"))


class KindLabels(unittest.TestCase):
    def test_any_kind_label_is_a_persons_choice(self):
        self.assertTrue(has_kind_label({"kind: refactor"}))
        self.assertTrue(has_kind_label({"kind: feature", "area: core"}))
        self.assertFalse(has_kind_label({"enhancement", "area: core"}))
        self.assertFalse(has_kind_label({"area: core", "good first issue"}))

    def test_kind_from_labels_reads_every_kind_label(self):
        self.assertEqual(kind_from_labels({"kind: feature"}), "feature")
        self.assertEqual(kind_from_labels({"kind: docs", "area: docs"}), "docs")
        self.assertEqual(kind_from_labels({"kind: simd"}), "perf")
        self.assertEqual(kind_from_labels({"kind: test"}), "other")
        self.assertEqual(kind_from_labels({"kind: bug", "kind: test"}), "bug")
        self.assertIsNone(kind_from_labels({"enhancement"}))


class ModelAnswer(unittest.TestCase):
    ANSWER = {"kind": "bug", "breaking": False, "areas": ["area: core"], "duplicate_of": None, "parent": 12,
              "split_into": ["", "Fix A"], "reason": "Wrong bytes."}

    def test_valid_answer(self):
        result = parse_classification(dict(self.ANSWER))
        self.assertEqual((result.kind, result.parent, result.split_into), ("bug", 12, ["Fix A"]))

    def test_new_area_is_a_label_suffix(self):
        self.assertIsNone(parse_classification(dict(self.ANSWER)).new_area)
        self.assertEqual(parse_classification({**self.ANSWER, "new_area": "area: ASP.NET Core!"}).new_area,
                         "asp-net-core")
        self.assertIsNone(parse_classification({**self.ANSWER, "new_area": "  ** "}).new_area)
        with self.assertRaises(TypeError):
            parse_classification({**self.ANSWER, "new_area": 3})

    def test_rejects_answers_outside_the_schema(self):
        for key, value in [("kind", "spam"), ("breaking", "yes"), ("parent", True), ("duplicate_of", "12"),
                           ("areas", [1])]:
            with self.subTest(key=key):
                with self.assertRaises((ValueError, TypeError)):
                    parse_classification({**self.ANSWER, key: value})


class Comment(unittest.TestCase):
    def test_no_comment_without_duplicate_or_split(self):
        self.assertIsNone(comment_body(Plan(issue=1, kind="bug", breaking=False, latest_tag=None, parent=3)))

    def test_comment_is_marked_and_neutralized(self):
        plan = Plan(issue=1, kind="bug", breaking=False, latest_tag=None, duplicate_of=7,
                    split_into=[neutralize("Ping @owner <img src=x> [link](https://e.vil)")], reason="@all see #7")
        body = comment_body(plan)
        self.assertTrue(body.startswith(COMMENT_MARKER))
        self.assertIn("#7", body)
        self.assertNotIn("@owner", body)
        self.assertNotIn("@all", body)
        self.assertNotIn("<img", body)
        self.assertNotIn("](", body)


class CommentAudience(unittest.TestCase):
    def test_only_issues_from_outside_the_maintainers_get_a_comment(self):
        for association in ("OWNER", "MEMBER", "COLLABORATOR"):
            self.assertFalse(wants_comment({"author_association": association}))
        for association in ("CONTRIBUTOR", "FIRST_TIME_CONTRIBUTOR", "NONE", None):
            self.assertTrue(wants_comment({"author_association": association}))


class NewAreaComment(unittest.TestCase):
    def test_suggests_a_new_area_without_creating_it(self):
        body = comment_body(Plan(issue=1, kind="feature", breaking=False, latest_tag=None, new_area="aspnetcore"))
        self.assertTrue(body.startswith(COMMENT_MARKER))
        self.assertIn("`area: aspnetcore`", body)


class FakeGitHub:
    def __init__(self, comments):
        self.comments = comments
        self.calls = []

    def paginate(self, path, limit=None):
        return self.comments

    def request(self, method, path, body=None):
        self.calls.append((method, path, body))


class CommentRerun(unittest.TestCase):
    BOT = {"type": "Bot"}

    def test_reruns_update_the_one_marked_comment(self):
        gh = FakeGitHub([{"id": 5, "user": {"type": "User"}, "body": COMMENT_MARKER + " quoted by a person"},
                         {"id": 9, "user": self.BOT, "body": COMMENT_MARKER + " old"}])
        upsert_comment(gh, 1, COMMENT_MARKER + " new")
        self.assertEqual(gh.calls, [("PATCH", "/issues/comments/9", {"body": COMMENT_MARKER + " new"})])

    def test_unchanged_comment_is_left_alone_and_a_first_run_posts(self):
        gh = FakeGitHub([{"id": 9, "user": self.BOT, "body": COMMENT_MARKER + " same"}])
        upsert_comment(gh, 1, COMMENT_MARKER + " same")
        self.assertEqual(gh.calls, [])
        gh = FakeGitHub([])
        upsert_comment(gh, 1, COMMENT_MARKER + " first")
        self.assertEqual(gh.calls, [("POST", "/issues/1/comments", {"body": COMMENT_MARKER + " first"})])


if __name__ == "__main__":
    unittest.main()
