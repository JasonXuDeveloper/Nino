#!/usr/bin/env bash
# Keeps the agent instructions indexed and complete:
#  - the root AGENTS.md lists every directory AGENTS.md (its "> Read when:" line) and every development skill (its
#    description's "Use when ..." part), between the agents-index / skills-index markers;
#  - each AGENTS.md has a CLAUDE.md next to it that is the one line @AGENTS.md (Claude Code imports it), and
#    .agents/skills links to .claude/skills (where Codex, Cursor and other agents look for skills);
#  - every development skill (.claude/skills) carries metadata.internal: true, so `npx skills add` offers only the
#    public skills of skills/; every skill's name is its directory's;
#  - every skill's description ends with a "Triggers:" list, and the prompt hook (.claude/hooks/skill-hint.py) names
#    the skill for at least one of its own trigger words, so every skill can load without being asked for by name;
#  - every entry of a mapped directory is named in its AGENTS.md's file map (each .cs file for Nino.Core and the
#    generator, each file or folder directly under the directory elsewhere).
# tools/agents.sh writes the indexes; tools/agents.sh --check fails when anything is stale or missing (CI, verify).
set -euo pipefail
cd "$(dirname "$0")/.."
python3 - "${1:-}" <<'PY'
import os, re, subprocess, sys
check = sys.argv[1] == "--check"
problems = []

files = subprocess.check_output(["git", "ls-files", "--cached", "--others", "--exclude-standard"], text=True).split()
present = [f for f in files if os.path.exists(f)]
dirs = sorted(f for f in present if f.endswith("/AGENTS.md"))
for f in dirs + ["AGENTS.md"]:
    claude = os.path.join(os.path.dirname(f), "CLAUDE.md")
    if not os.path.isfile(claude) or open(claude).read().strip() != "@AGENTS.md":
        problems.append(f"{claude} must be the one line @AGENTS.md")
if not (os.path.islink(".agents/skills") and os.readlink(".agents/skills") == "../.claude/skills"):
    problems.append(".agents/skills must be a symbolic link to ../.claude/skills")

def front(path):
    text = open(path).read()
    m = re.match(r"---\n(.*?)\n---\n", text, re.S)
    if not m:
        problems.append(f"{path} has no frontmatter")
        return None, "", ""
    head = m.group(1)
    name = re.search(r"^name: (.+)$", head, re.M)
    desc = re.search(r"^description: (.+)$", head, re.M)
    if not name or not desc:
        problems.append(f"{path} needs name and description")
        return None, "", head
    return name.group(1).strip(), desc.group(1).strip().strip('"').replace('\\"', '"'), head

rows = ["| Directory | Read its AGENTS.md when |", "|---|---|"]
for f in dirs:
    m = re.search(r"^> Read when: (.+)$", open(f).read(), re.M)
    if not m:
        problems.append(f"{f} has no '> Read when:' line")
        continue
    rows.append(f"| [`{os.path.dirname(f)}/`]({f}) | {m.group(1).rstrip('.')} |")
agents = "\n".join(rows)

rows = ["| Skill | What it does | Use it |", "|---|---|---|"]
for f in sorted(p for p in present if re.fullmatch(r"\.claude/skills/[^/]+/SKILL\.md", p)):
    name, desc, head = front(f)
    if name is None:
        continue
    if name != f.split("/")[2]:
        problems.append(f"{f}: name {name} is not its directory's")
    if not re.search(r"^metadata:\n\s+internal: true$", head, re.M):
        problems.append(f"{f}: a development skill needs metadata.internal: true")
    m = re.search(r"\b(Use (?:when|for|in|once|before|after)\b.*?)(?: Triggers:|$)", desc)
    if not m:
        problems.append(f"{f}: the description has no 'Use when ...' sentence")
        continue
    what = re.split(r" - |\. ", desc, maxsplit=1)[0].rstrip(".")
    use = re.sub(r"^Use ", "", m.group(1)).rstrip(".")
    rows.append(f"| [`{name}`]({f}) | {what} | {use} |")
skills = "\n".join(rows)
for f in sorted(p for p in present if re.fullmatch(r"skills/[^/]+/SKILL\.md", p)):
    name, desc, head = front(f)
    if name is not None and name != f.split("/")[1]:
        problems.append(f"{f}: name {name} is not its directory's")
    if "internal: true" in head:
        problems.append(f"{f}: a public skill must not be internal")

import json
for f in sorted(p for p in present if re.fullmatch(r"(\.claude/)?skills/[^/]+/SKILL\.md", p)):
    name, desc, head = front(f)
    if name is None:
        continue
    if "Triggers:" not in desc:
        problems.append(f"{f}: the description has no 'Triggers:' list (the prompt hook reads it)")
        continue
    words = [w.strip().strip('"') for w in desc.split("Triggers:", 1)[1].rstrip(". ").split(",") if w.strip()]
    def hinted(word):
        event = {"hook_event_name": "UserPromptSubmit", "session_id": f"agents-check-{os.getpid()}-{name}-{abs(hash(word))}", "prompt": word}
        out = subprocess.run(["python3", ".claude/hooks/skill-hint.py"], input=json.dumps(event), capture_output=True, text=True,
                             env={**os.environ, "CLAUDE_PROJECT_DIR": os.getcwd()}).stdout
        return re.search(rf"(?<![\w-]){re.escape(name)}(?![\w-])", out) is not None
    if not any(hinted(w) for w in words[:8]):
        problems.append(f"{f}: the prompt hook names this skill for none of its first trigger words")

root = open("AGENTS.md").read()
def put(text, key, body):
    pat = re.compile(rf"(<!-- {key}:start -->\n).*?(<!-- {key}:end -->)", re.S)
    if not pat.search(text):
        problems.append(f"AGENTS.md has no {key} markers")
        return text
    return pat.sub(lambda m: m.group(1) + body + "\n" + m.group(2), text)
new = put(put(root, "agents-index", agents), "skills-index", skills)
if new != root:
    if check: problems.append("AGENTS.md indexes are stale: run tools/agents.sh")
    else: open("AGENTS.md", "w").write(new)

# File maps: a new file or folder is named in its directory's AGENTS.md.
deep = {"src/Nino.Core", "src/Nino.Generator"}
skip = {"AGENTS.md", "CLAUDE.md", "bin", "obj", "node_modules", "target"}
for f in dirs:
    d = os.path.dirname(f)
    text = open(f).read()
    if d in deep:
        names = {os.path.basename(p) for p in present if p.startswith(d + "/") and p.endswith(".cs")}
    else:
        names = {p[len(d) + 1:].split("/")[0] for p in present if p.startswith(d + "/")}
    for n in sorted(names - skip):
        if n.endswith(".meta"):
            continue
        if n not in text:
            problems.append(f"{d}/{n} is not in the file map {f}")

for p in problems: print(p)
print(f"agents: {len(problems)} problems")
sys.exit(1 if problems else 0)
PY
