#!/usr/bin/env python3
"""Write the GitHub release notes of a Nino tag: the highlights, every commit since the start tag, grouped, plus all
contributors.

Usage (in the release workflow, after the tag is pushed):
  python3 .github/scripts/release_notes.py --tag v4.0.0 [--since v3.3.13] [--header FILE] --out notes.md
Prints the start tag on stderr. Needs git history with tags (fetch-depth: 0) and `gh` with GH_TOKEN.

Start tag (unless --since is given):
  prerelease x.y.z-label.n -> the newest release tag of any kind below it (v4.0.0-rc.2 -> v4.0.0-rc.1)
  stable x.y.z             -> the newest *stable* tag below it          (v4.0.1 -> v4.0.0, v4.1.0 -> v4.0.x)
Tags matching IGNORE (Unity/OpenUPM -preview.N tags, the v3.9.x v4 test builds) are never a start tag, so
v4.0.0 starts at v3.3.13. Only tags that are ancestors of --tag are candidates.

Highlights come from trailers in the commit messages of the range, so nothing is written by hand at release time.
Put them in the message's last paragraph (the trailer block, next to Co-Authored-By); a value may go on over
indented lines:
  Wire-Format: what now writes different bytes, and since when   -> "Wire format"
  Added: a new API, attribute or behavior users can rely on       -> "New"
  Fixed: a user-visible bug that is fixed                         -> "Fixes"
Each entry gets the issues the commit closes ("Closes #202" in its body, outside the trailer block) unless it names
them already. A release without such trailers has no highlights.
"""
import argparse, fnmatch, json, os, re, subprocess, sys
from collections import OrderedDict

REPO = os.environ.get("GITHUB_REPOSITORY", "JasonXuDeveloper/Nino")
IGNORE = ["v*-preview.*", "v3.9.*"]
BODY_LIMIT = 125_000          # GitHub rejects a longer release body (422)
RESERVED = 45_000             # left for report.yml's v3 section and Performance Report (~31k today)
TAG_RE = re.compile(r"^v(\d+)\.(\d+)\.(\d+)(?:-(alpha|beta|rc)\.(\d+))?$")
LABEL_RANK = {"alpha": 0, "beta": 1, "rc": 2, None: 3}
# Commit trailer -> highlights section, in this order.
HIGHLIGHTS = [("Wire-Format", "Wire format"), ("Added", "New"), ("Fixed", "Fixes")]
CHANGELOG = "https://nino.xgamedev.net/en/docs/v4/changelog"
CLOSES = re.compile(r"\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s+#(\d+)", re.I)

# Release machinery and merge commits: never listed (their changes are the commits themselves).
SKIP = re.compile(r"^(Bump (to|version to) v|(Bump|Update) Unity Package DLLs|Update README benchmark numbers"
                  r"|Merge (pull request|branch|remote-tracking branch)|release v\d)", re.I)
# (group, pattern on the subject with any "Area:" / "type(scope):" prefix removed, prefix pattern)
GROUPS = [
    ("⚠️ Breaking changes", None, None),
    ("✨ Features", r"^(add|support|allow|introduce|implement|enable|new|rewrite|let|complete|generate|register)\b",
     r"^feat"),
    # Filled by classify() only: an "Area:" commit to the library whose summary starts with no verb above.
    ("📦 Runtime, generator and analyzer", None, None),
    ("⚡ Performance", r"^(speed|faster|optimi[sz]e|reduce|avoid|inline|unroll|cache|shrink|trim|reuse|store|copy"
     r"|keep .* in registers|align|tiny optimi|optimi[sz]ation)", r"^perf"),
    ("🐛 Fixes", r"^(fix|bug|correct|handle|prevent|restore|revert|resolve|ensure|guard)\b", r"^fix"),
    ("♻️ Refactoring and cleanup", r"^(rename|remove|move|simplif|clean|split|merge|drop|replace|unify|refactor|one "
     r"|use|extract|share|separate|seperate|migrate)", r"^(refactor|.*cleanup)$"),
    ("📖 Documentation", r"^(document|describe|present|docs?\b|readme|update readme)", r"^(docs?|readme)$"),
    ("🧪 Tests", r"^(test|cover)", r"^tests?$"),
    # Not "release" alone: "Core: release collectible assemblies" is not about releasing.
    ("🔧 Build, CI and dependencies", r"^(ci|build|workflow|dependabot|bump|update .* dependenc|update \S+\.ya?ml|.*\bci\b)",
     r"^(ci|build|chore|release|dependabot|benchmarks?|\.git\w*)$"),
    ("Other changes", r"", None),  # catch-all
]
# "Area:" prefixes of the library's own code (the rest of a subject there rarely starts with a verb).
LIBRARY = re.compile(r"^(core|runtime|generator|analy[sz]er|immutable|interceptors?|serializer|deserializer)$", re.I)
AI_OR_BOT = re.compile(r"\[bot\]|^(claude|copilot|codex|gemini)$|noreply@anthropic\.com|@openai\.com|gemini@google\.com", re.I)


def sh(*args):
    return subprocess.run(args, check=True, capture_output=True, text=True).stdout


def gh(path, *extra):
    return json.loads(sh("gh", "api", path, *extra) or "null")


def version_key(tag):
    m = TAG_RE.match(tag)
    a, b, c, label, n = m.groups()
    return (int(a), int(b), int(c), LABEL_RANK[label], int(n or 0))


def start_tag(tag, ref):
    prerelease = TAG_RE.match(tag).group(4) is not None
    candidates = []
    for t in sh("git", "tag", "--list", "v*").split():
        if t == tag or not TAG_RE.match(t) or any(fnmatch.fnmatch(t, p) for p in IGNORE):
            continue
        if version_key(t) >= version_key(tag) or (not prerelease and TAG_RE.match(t).group(4)):
            continue
        if subprocess.run(["git", "merge-base", "--is-ancestor", t, ref]).returncode == 0:
            candidates.append(t)
    return max(candidates, key=version_key) if candidates else None


def commits(rng):
    sep, end = "\x1f", "\x1e"
    trailer = "%(trailers:key={},valueonly,unfold,separator=%x1d)"
    fmt = sep.join(["%H", "%h", "%an", "%ae", "%s", trailer.format("Co-authored-by"), "%P", "%b"] +
                   [trailer.format(key) for key, _ in HIGHLIGHTS])
    out = []
    for rec in sh("git", "log", "--reverse", f"--format={fmt}{end}", rng).split(end):
        if not rec.strip():
            continue
        h, short, an, ae, s, co, parents, body, *notes = rec.strip("\n").split(sep)
        out.append(dict(sha=h, short=short, name=an, email=ae, subject=s, merge=len(parents.split()) > 1,
                        coauthors=[c.strip() for c in co.split("\x1d") if c.strip()],
                        closes=list(dict.fromkeys(CLOSES.findall(body))),
                        highlights={key: [n.strip() for n in note.split("\x1d") if n.strip()]
                                    for (key, _), note in zip(HIGHLIGHTS, notes)}))
    return out


def highlights(all_commits, since):
    """The Wire format / New / Fixes sections from the commits' trailers; empty when there are none."""
    sections = []
    for key, title in HIGHLIGHTS:
        entries = []
        for c in all_commits:
            for note in c["highlights"][key]:
                issues = [f"#{n}" for n in c["closes"] if not re.search(rf"#{n}\b", note)]
                entries.append(f"- {note}{' (' + ', '.join(issues) + ')' if issues else ''}")
        if entries:
            intro = f"Only these write different bytes than {since}:\n\n" if key == "Wire-Format" and since else ""
            sections.append(f"### {title}\n\n{intro}" + "\n".join(entries) + "\n")
    if not sections:
        return ""
    return "\n".join(sections) + f"\nEvery change between v4 releases: [Changelog]({CHANGELOG}).\n\n"


def classify(subject):
    if re.search(r"^\w+(\([^)]*\))?!:|BREAKING", subject):
        return GROUPS[0][0]
    if re.search(r"\.git(attributes|ignore)\b", subject):
        return "🔧 Build, CI and dependencies"
    m = re.match(r"^([.A-Za-z][\w ,./-]{0,30}?)(\([^)]*\))?:\s*(.*)$", subject)
    prefix, rest = (m.group(1).strip().lower(), m.group(3)) if m else ("", subject)
    for title, pattern, prefix_pattern in GROUPS[1:]:
        if prefix_pattern and prefix and re.match(prefix_pattern, prefix, re.I):
            return title
    for title, pattern, _ in GROUPS[1:-1]:
        if pattern is not None and re.match(pattern, rest.strip(), re.I):
            return title
    if LIBRARY.match(prefix):
        return "📦 Runtime, generator and analyzer"
    return GROUPS[-1][0]


class Logins:
    """Commit author/co-author -> GitHub login. Commit API first, then noreply addresses, then PR authors."""

    def __init__(self):
        self.by_email = {}

    def from_noreply(self, email):
        m = re.match(r"^(?:\d+\+)?([^@]+)@users\.noreply\.github\.com$", email, re.I)
        return m.group(1) if m else None

    def author(self, c):
        key = c["email"].lower()
        if key not in self.by_email:
            login = self.from_noreply(c["email"])
            if not login:
                data = gh(f"repos/{REPO}/commits/{c['sha']}")
                login = (data.get("author") or {}).get("login")
            self.by_email[key] = login
        return self.by_email[key]

    def coauthor(self, trailer):
        m = re.match(r"^(.*?)\s*<([^>]+)>$", trailer)
        if not m:
            return None, trailer
        name, email = m.group(1), m.group(2)
        return self.by_email.get(email.lower()) or self.from_noreply(email), name


def pr_numbers(sha):
    try:
        pulls = [p for p in gh(f"repos/{REPO}/commits/{sha}/pulls") if p.get("merged_at")]
    except subprocess.CalledProcessError:
        return [], []
    return [p["number"] for p in pulls], [p["user"]["login"] for p in pulls]


def new_contributors(tag, since):
    """GitHub's own "New Contributors" block (first merged PR in the repository), for the same range."""
    try:
        body = gh(f"repos/{REPO}/releases/generate-notes", "-f", f"tag_name={tag}",
                  "-f", f"previous_tag_name={since}")["body"]
    except subprocess.CalledProcessError:
        return ""
    m = re.search(r"^## New Contributors\n(.*?)(?=^\*\*Full Changelog|\Z)", body, re.S | re.M)
    return m.group(1).strip() if m else ""


def render(groups, budget_compact):
    """Each group as a list. budget_compact: 0 full, 1 collapse big groups, 2 count-only for low-value groups."""
    out = []
    for title, lines in groups.items():
        if not lines:
            continue
        low_value = title.startswith(("🔧", "🧪", "📖"))
        if budget_compact >= 2 and low_value:
            out.append(f"**{title}**: {len(lines)} commits (see the full changelog link below)\n")
            continue
        body = "\n".join(lines)
        if budget_compact >= 1 and (len(lines) > 15 or low_value):
            out.append(f"<details>\n<summary><b>{title}</b> ({len(lines)})</summary>\n\n{body}\n\n</details>\n")
        else:
            out.append(f"### {title}\n\n{body}\n")
    return "\n".join(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tag", required=True)
    ap.add_argument("--ref", help="commit the tag points at (default: the tag; for dry runs before tagging)")
    ap.add_argument("--since", help="start tag (default: chosen from the tag list)")
    ap.add_argument("--header", help="Markdown placed first (the install line)")
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    ref = args.ref or args.tag
    since = args.since or start_tag(args.tag, ref)
    # Tags are vX.Y.Z: accept the version alone too ("3.3.13").
    if args.since and subprocess.run(["git", "rev-parse", "-q", "--verify", f"refs/tags/{since}"],
                                     capture_output=True).returncode != 0:
        since = f"v{since}"
    print(f"start tag: {since}", file=sys.stderr)
    rng = f"{since}..{ref}" if since else ref
    all_commits = commits(rng)

    logins, people = Logins(), OrderedDict()      # login (or plain name) -> commit count
    groups = OrderedDict((g[0], []) for g in GROUPS)
    listed = 0

    def credit(login, name, email=""):
        if AI_OR_BOT.search(login or "") or AI_OR_BOT.search(name) or AI_OR_BOT.search(email):
            return
        key = f"@{login}" if login else name
        key = next((k for k in people if k.lower() == key.lower()), key)   # logins are case-insensitive
        people[key] = people.get(key, 0) + 1

    from concurrent.futures import ThreadPoolExecutor
    with ThreadPoolExecutor(8) as pool:          # ~2 API calls per commit; sequential takes minutes
        pulls = list(pool.map(lambda c: ([], []) if c["merge"] else pr_numbers(c["sha"]), all_commits))
        list(pool.map(logins.author, {c["email"].lower(): c for c in all_commits}.values()))
    for c, (prs, pr_authors) in zip(all_commits, pulls):
        login = logins.author(c)
        if not login and pr_authors:             # an email linked to no account: credit the PR author (#158)
            login = pr_authors[0]
        credit(login, c["name"], c["email"])
        for trailer in c["coauthors"]:
            co_login, co_name = logins.coauthor(trailer)
            credit(co_login, co_name, trailer)
        if c["merge"] or SKIP.match(c["subject"]):
            continue
        who = f"@{login}" if login else c["name"]
        if login and AI_OR_BOT.search(login):
            who = ""
        refs = " ".join([c["short"]] + [f"#{n}" for n in prs])
        text = c["subject"].replace("<", "&lt;").replace(">", "&gt;")  # List<T> would be an HTML tag
        text = text.replace("Performance Report", "performance report")  # report.yml and the docs look for it
        groups[classify(c["subject"])].append(f"* {text} ({refs}){' by ' + who if who else ''}")
        listed += 1

    header = open(args.header, encoding="utf-8").read().strip() + "\n\n" if args.header else ""
    header += highlights(all_commits, since)
    compare = f"https://github.com/{REPO}/compare/{since}...{args.tag}" if since else ""
    contributors = ", ".join(sorted(people, key=lambda k: (-people[k], k.lower())))
    newbies = new_contributors(args.tag, since) if since else ""
    tail = ""
    if newbies:
        tail += f"## New Contributors\n\n{newbies}\n\n"
    tail += f"## Contributors\n\nThanks to everyone who contributed to this release: {contributors}\n\n"
    if compare:
        tail += f"**Full Changelog**: {compare}\n"

    title = f"## What's Changed since {since}\n\n{listed} commits" if since else "## What's Changed\n\n"
    budget = BODY_LIMIT - RESERVED
    for compact in (0 if listed <= 60 else 1, 1, 2):
        body = f"{header}{title}\n\n{render(groups, compact)}\n{tail}"
        if len(body) <= budget:
            break
    else:  # still too long: keep the head of the list and point at the compare view
        cut = budget - len(tail) - 200
        body = body[:cut].rsplit("\n* ", 1)[0] + f"\n\n…truncated; see the full changelog: {compare}\n\n</details>\n\n" + tail
    open(args.out, "w", encoding="utf-8").write(body)
    print(f"{listed} commits listed, {len(people)} contributors, {len(body)} chars", file=sys.stderr)


if __name__ == "__main__":
    main()
