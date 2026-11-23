#!/usr/bin/env python3
"""Builds the change list that RemoteMan shows in its "What's new" tab after an update.

Walks the branch's first-parent history (one entry per merged pull request, which on the test branch is one
test build), takes each pull request's title and, when it has one, the bullets under a "## What's new" heading
in its description. Writes src/RemoteMan.App/changelog.json, which the app embeds, and optionally Markdown
release notes for the GitHub release.

    python build/changelog.py --version 0.1.0-ci.98 [--notes release-notes.md]

Pull request descriptions come from the GitHub API when GH_TOKEN (or GITHUB_TOKEN) and GITHUB_REPOSITORY are
set; without them the titles from the merge commits are used. A "What's new" section that says just "none"
leaves that pull request out of the tab (CI tweaks, refactors).
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_OUT = ROOT / "src" / "RemoteMan.App" / "changelog.json"
MERGE_PR = re.compile(r"^Merge pull request #(\d+) from ")
SQUASH_PR = re.compile(r"^(.*) \(#(\d+)\)$")
SECTION = re.compile(r"^#{2,3}\s*(what'?s new|release notes|changelog)\s*$", re.IGNORECASE)
HEADING = re.compile(r"^#{1,6}\s")
BULLET = re.compile(r"^\s*[-*+]\s+(.*)$")


def git(*args: str) -> str:
    return subprocess.run(["git", *args], cwd=ROOT, check=True, capture_output=True, text=True, encoding="utf-8").stdout


def commits(limit: int) -> list[dict]:
    raw = git("log", "--first-parent", f"-n{limit}", "--format=%H%x1f%cI%x1f%s%x1f%b%x1e")
    out = []
    for rec in raw.split("\x1e"):
        rec = rec.strip("\n")
        if not rec:
            continue
        sha, date, subject, body = (rec.split("\x1f") + [""])[:4]
        out.append({"sha": sha, "date": date, "subject": subject.strip(), "body": body.strip()})
    return out


def tag_versions() -> dict[str, str]:
    """Commit -> version of the build published from it (test-0.1.0-ci.97, v0.2.0)."""
    versions: dict[str, str] = {}
    try:
        raw = git("tag", "--list", "test-*", "v*", "--format=%(objectname) %(*objectname) %(refname:short)")
    except subprocess.CalledProcessError:
        return versions
    for line in raw.splitlines():
        parts = line.split()
        if len(parts) == 3:
            obj, peeled, name = parts
        elif len(parts) == 2:
            obj, name = parts
            peeled = ""
        else:
            continue
        version = name.removeprefix("test-").removeprefix("v")
        versions.setdefault(peeled or obj, version)
    return versions


def fetch_prs() -> dict[int, dict]:
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    repo = os.environ.get("GITHUB_REPOSITORY")
    if not token or not repo:
        return {}
    prs: dict[int, dict] = {}
    for page in (1, 2):
        url = f"https://api.github.com/repos/{repo}/pulls?state=closed&sort=updated&direction=desc&per_page=100&page={page}"
        req = urllib.request.Request(url, headers={
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github+json",
            "User-Agent": "remoteman-changelog",
        })
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                batch = json.load(resp)
        except Exception as ex:  # offline, rate limit: titles from git are enough
            print(f"changelog: couldn't read pull requests ({ex}); using commit titles", file=sys.stderr)
            break
        for pr in batch:
            prs[pr["number"]] = {"title": pr.get("title") or "", "body": pr.get("body") or ""}
        if len(batch) < 100:
            break
    return prs


def clean(text: str) -> str:
    text = re.sub(r"\[([^\]]+)\]\([^)]+\)", r"\1", text)   # [link](url) -> link
    text = text.replace("**", "").replace("__", "").replace("`", "")
    return re.sub(r"\s+", " ", text).strip()


def whats_new(body: str) -> list[str] | None:
    """Bullets under the description's "What's new" heading; [] when it says none; None when there is no section."""
    lines = body.replace("\r\n", "\n").split("\n")
    for i, line in enumerate(lines):
        if not SECTION.match(line.strip()):
            continue
        notes: list[str] = []
        text: list[str] = []
        for nxt in lines[i + 1:]:
            if HEADING.match(nxt.strip()) or nxt.strip() == "---":
                break
            m = BULLET.match(nxt)
            if m:
                notes.append(clean(m.group(1)))
            elif nxt.strip() and not nxt.strip().startswith("<!--"):
                if notes and nxt.startswith((" ", "\t")):
                    notes[-1] = clean(notes[-1] + " " + nxt)
                else:
                    text.append(nxt.strip())
        if not notes and text:
            notes = [clean(" ".join(text))]
        notes = [n for n in notes if n]
        if len(notes) == 1 and notes[0].strip(" .").lower() in ("none", "n/a", "nothing"):
            return []
        return notes or None  # an empty section (the template left as is) shows just the title
    return None


def build(limit: int, head_version: str) -> list[dict]:
    versions = tag_versions()
    head = git("rev-parse", "HEAD").strip()
    versions.setdefault(head, head_version)  # this build is published from HEAD
    prs = fetch_prs()
    items = []
    for c in commits(limit):
        subject, body = c["subject"], c["body"]
        pr = None
        title = subject
        m = MERGE_PR.match(subject)
        if m:
            pr = int(m.group(1))
            title = body.split("\n", 1)[0].strip() or subject
        elif subject.startswith("Merge "):
            continue  # merging main into a branch and the like: not a change of its own
        else:
            m = SQUASH_PR.match(subject)
            if m:
                title, pr = m.group(1), int(m.group(2))

        notes: list[str] = []
        if pr is not None and pr in prs:
            title = prs[pr]["title"] or title
            found = whats_new(prs[pr]["body"])
            if found == []:
                continue
            notes = found or []
        item = {
            "key": f"pr:{pr}" if pr is not None else c["sha"][:12],
            "title": clean(title),
            "notes": notes,
            "date": c["date"],
        }
        if pr is not None:
            item["pr"] = pr
        if c["sha"] in versions:
            item["version"] = versions[c["sha"]]
        items.append(item)
    return items


def release_notes(version: str, items: list[dict], count: int = 15) -> str:
    out = [f"## RemoteMan {version}", ""]
    for it in items[:count]:
        out.append(f"- {it['title']}" + (f" (#{it['pr']})" if "pr" in it else ""))
        out.extend(f"  - {n}" for n in it["notes"])
    return "\n".join(out) + "\n"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", required=True, help="the version being built")
    ap.add_argument("--out", default=str(DEFAULT_OUT))
    ap.add_argument("--notes", help="also write Markdown release notes here")
    ap.add_argument("--limit", type=int, default=80, help="how many first-parent commits to look at")
    args = ap.parse_args()

    items = build(args.limit, args.version)
    Path(args.out).write_text(json.dumps({"version": args.version, "items": items}, indent=1, ensure_ascii=False), encoding="utf-8")
    if args.notes:
        Path(args.notes).write_text(release_notes(args.version, items), encoding="utf-8")
    print(f"changelog: {len(items)} changes for {args.version}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
