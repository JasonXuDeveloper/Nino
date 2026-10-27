#!/usr/bin/env python3
"""Names the Nino skills (and the directory AGENTS.md) a task needs, so they load even when the model would not have
picked them from their descriptions alone.

- UserPromptSubmit: the skills whose "Triggers:" list (in each SKILL.md's description, the one source of the words)
  matches the prompt, most matches first.
- PreToolUse: before the first edit of a file in an area, the skills that govern it and the nearest AGENTS.md;
  before a commit or push, `verify`.

Each skill or file is named once per session. Reads the hook's JSON on stdin; prints nothing when there is nothing new
to say, and never blocks."""
import glob, json, os, re, sys, tempfile

AREAS = [  # (path prefix, skills), most specific first; every match applies
    ("src/Nino.Generator/Analysis/", ["audit-analyzer"]),
    ("src/Nino.Generator.CodeFixes/", ["audit-analyzer"]),
    ("src/Nino.Generator/Generation/Emit/", ["audit-generated-code", "optimize"]),
    ("src/Nino.Generator", ["audit-generator", "feature"]),
    ("src/Nino.Codegen", ["audit-runtimes", "audit-generated-code"]),
    ("src/Nino.Core/Attributes/", ["audit-naming", "audit-docs"]),
    ("src/Nino.Core/", ["audit-core", "optimize", "audit-security", "feature"]),
    ("src/Nino.Immutable/", ["audit-core", "runtime-internals", "optimize", "feature"]),
    ("src/Nino.AspNetCore/", ["audit-core", "feature"]),
    ("src/Nino.UnityTypes/", ["audit-core", "platforms"]),
    ("src/Nino.Unity/", ["platforms", "audit-code"]),
    ("runtimes/go/bench", ["benchmark"]),
    ("runtimes/rust/bench", ["benchmark"]),
    ("runtimes/typescript/bench", ["benchmark"]),
    ("runtimes/", ["audit-runtimes", "optimize", "audit-security", "feature"]),
    ("conformance/", ["wire-format", "audit-security"]),
    ("docs/spec/", ["wire-format"]),
    ("tests/Nino.RuntimeTests/", ["runtime-internals", "platforms"]),
    ("tests/Nino.Fuzz/", ["audit-security"]),
    ("tests/", ["feature"]),
    ("benchmarks/", ["benchmark", "audit-code"]),
    (".github/ab-request.txt", ["benchmark"]),
    (".github/", ["audit-ci"]),
    ("tools/", ["audit-code"]),
    ("skills/", ["audit-docs"]),
    ("docs/", ["audit-docs"]),
    ("README", ["audit-docs"]),
    ("CONTRIBUTING.md", ["audit-docs"]),
    (".claude/", ["audit-layout"]),
    ("AGENTS.md", ["audit-layout"]),
]
MAX_PROMPT_SKILLS = 4


def triggers(root):
    """{skill: [trigger words]} from the "Triggers:" sentence of every skill's description."""
    found = {}
    for path in glob.glob(os.path.join(root, ".claude/skills/*/SKILL.md")) + glob.glob(os.path.join(root, "skills/*/SKILL.md")):
        try:
            text = open(path, encoding="utf-8").read()
        except OSError:
            continue
        m = re.search(r"^description:\s*\"?(.*?)\"?\s*$", text, re.M)
        if not m or "Triggers:" not in m.group(1):
            continue
        words = m.group(1).split("Triggers:", 1)[1].rstrip(". ")
        found[os.path.basename(os.path.dirname(path))] = [w.strip().strip('"').strip("*") for w in words.split(",") if w.strip()]
    return found


def matches(word, prompt):
    if not word:
        return False
    if not word.isascii():
        return word in prompt
    if re.fullmatch(r"[A-Za-z][A-Za-z0-9 \-]*", word):  # a word or phrase: whole words only
        return re.search(r"(?<![A-Za-z0-9])" + re.escape(word.lower()) + r"(?![A-Za-z0-9])", prompt.lower()) is not None
    return word.lower() in prompt.lower()  # a path, file name or symbol


def prompt_skills(root, prompt):
    scored = []
    for skill, words in triggers(root).items():
        hits = sum(1 for w in words if matches(w, prompt))
        if hits:
            scored.append((-hits, skill))
    return [skill for _, skill in sorted(scored)[:MAX_PROMPT_SKILLS]]


def edit_hints(root, data):
    path = data.get("file_path") or data.get("notebook_path") or ""
    rel = os.path.relpath(os.path.abspath(path), root) if path else ""
    if not rel or rel.startswith(".."):
        return [], None
    skills = []
    for prefix, names in AREAS:
        if rel.startswith(prefix) or os.path.basename(rel).startswith(prefix):
            skills += [n for n in names if n not in skills]
    d = os.path.dirname(rel)
    while d:
        if os.path.isfile(os.path.join(root, d, "AGENTS.md")):
            return skills, os.path.join(d, "AGENTS.md")
        d = os.path.dirname(d)
    return skills, None


def main():
    try:
        event = json.load(sys.stdin)
    except ValueError:
        return
    root = os.environ.get("CLAUDE_PROJECT_DIR") or event.get("cwd") or os.getcwd()
    name = event.get("hook_event_name", "PreToolUse")
    skills, agents = [], None
    if name == "UserPromptSubmit":
        skills = prompt_skills(root, event.get("prompt", ""))
    elif event.get("tool_name") == "Bash":
        command = (event.get("tool_input") or {}).get("command", "")
        if "git push" in command or "git commit" in command:
            skills = ["verify"]
    else:
        skills, agents = edit_hints(root, event.get("tool_input") or {})

    seen_file = os.path.join(tempfile.gettempdir(), f"nino-skill-hints-{event.get('session_id', 'none')}")
    try:
        seen = set(open(seen_file).read().split())
    except OSError:
        seen = set()
    new = [s for s in skills if s not in seen]
    new_agents = agents if agents and agents not in seen else None
    if not new and not new_agents:
        return
    parts = []
    if new:
        parts.append("Load these Nino skills with the Skill tool before continuing, if not loaded yet: " + ", ".join(new) + ".")
    if new_agents:
        parts.append(f"Read {new_agents} in full first: the file map and local rules of this directory.")
    with open(seen_file, "a") as f:
        f.write(" ".join(new + ([new_agents] if new_agents else [])) + " ")
    print(json.dumps({"hookSpecificOutput": {"hookEventName": name, "additionalContext": " ".join(parts)}}))


main()
