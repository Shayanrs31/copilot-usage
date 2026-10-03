#!/usr/bin/env python3
"""Summarise GitHub Copilot Chat usage from your local VS Code chat history.

Read-only. VS Code keeps each Copilot Chat session on disk under
<user data>/User/workspaceStorage/<id>/chatSessions/. Newer versions write an operation log
(.jsonl: kind 0 = full state, 1 = set at path, 2 = append to array, 3 = delete); older versions
write one JSON document (.json). This script rebuilds each session and reports prompt tokens,
credits, models, what fills the prompt, instruction files, agents and tools.

Examples:
    python copilot_usage.py                      # the workspace for the current folder
    python copilot_usage.py --repo ~/src/my-app  # the workspace for another folder
    python copilot_usage.py --list               # every workspace that has chat history
    python copilot_usage.py --all                # every workspace together
    python copilot_usage.py --since 2026-10-01 --until 2026-10-31

Output: summary.txt, sessions.csv and requests.csv in --out (default: a folder in the temp dir).
Message text is not written unless you pass --include-messages.

This reads internal VS Code storage, not a documented API. It can change with any release.
"""
import argparse
import csv
import json
import os
import platform
import re
import statistics
import sys
import tempfile
from collections import Counter, defaultdict
from datetime import datetime, timezone
from urllib.parse import unquote, urlparse
from urllib.request import url2pathname

EDITIONS = ("Code", "Code - Insiders", "VSCodium", "Code - OSS")


def user_data_roots():
    """The User folder of each VS Code edition that exists on this machine."""
    system = platform.system()
    if system == "Windows":
        base = os.environ.get("APPDATA", "")
    elif system == "Darwin":
        base = os.path.expanduser("~/Library/Application Support")
    else:
        base = os.environ.get("XDG_CONFIG_HOME") or os.path.expanduser("~/.config")
    roots = []
    for edition in EDITIONS:
        user = os.path.join(base, edition, "User")
        if os.path.isdir(user):
            roots.append((edition, user))
    return roots


def uri_to_path(uri):
    """file:// URI from workspace.json -> local path. Returns None for remote workspaces."""
    parsed = urlparse(uri)
    if parsed.scheme != "file":
        return None
    path = url2pathname(unquote(parsed.path))
    if platform.system() == "Windows" and re.match(r"^\\[A-Za-z]:", path):
        path = path[1:]
    return os.path.normcase(os.path.abspath(path))


def workspaces():
    """Yield (edition, label, local path or None, chatSessions folder) for each workspace with chats."""
    for edition, user in user_data_roots():
        storage = os.path.join(user, "workspaceStorage")
        if os.path.isdir(storage):
            for entry in sorted(os.listdir(storage)):
                chats = os.path.join(storage, entry, "chatSessions")
                meta = os.path.join(storage, entry, "workspace.json")
                if not os.path.isdir(chats):
                    continue
                uri = ""
                if os.path.isfile(meta):
                    try:
                        with open(meta, encoding="utf-8") as fh:
                            data = json.load(fh)
                        uri = data.get("folder") or data.get("workspace") or data.get("configuration") or ""
                    except (OSError, ValueError):
                        pass
                label = unquote(uri) if uri else entry
                yield edition, label, uri_to_path(uri) if uri else None, chats
        empty = os.path.join(user, "globalStorage", "emptyWindowChatSessions")
        if os.path.isdir(empty):
            yield edition, "(windows with no folder open)", None, empty


def apply(state, op):
    kind, path, val = op.get("kind"), op.get("k") or [], op.get("v")
    if kind == 0:
        return val
    parent = state
    for key in path[:-1]:
        parent = parent[key]
    last = path[-1] if path else None
    if kind == 1:
        parent[last] = val
    elif kind == 2:
        arr = parent.setdefault(last, []) if isinstance(parent, dict) else parent[last]
        if op.get("i") is not None:
            del arr[op["i"]:]
        arr.extend(val if isinstance(val, list) else [val])
    elif kind == 3 and isinstance(parent, dict):
        parent.pop(last, None)
    return state


def load_session(path):
    """Rebuild one session from an operation log (.jsonl) or a full document (.json)."""
    with open(path, encoding="utf-8", errors="replace") as fh:
        if path.endswith(".json"):
            try:
                return json.load(fh)
            except ValueError:
                return {}
        state = {}
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                state = apply(state, json.loads(line))
            except (KeyError, IndexError, TypeError, ValueError):
                continue
        return state if isinstance(state, dict) else {}


def tool_calls(response):
    for part in response or []:
        if isinstance(part, dict) and part.get("kind") in ("toolInvocationSerialized", "toolInvocation"):
            yield part.get("toolId") or "?"


def median(values, default=0):
    return statistics.median(values) if values else default


def parse_date(text):
    return datetime.strptime(text, "%Y-%m-%d").date() if text else None


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    scope = parser.add_mutually_exclusive_group()
    scope.add_argument("--repo", help="folder whose VS Code workspace to read (default: current folder)")
    scope.add_argument("--all", action="store_true", help="read every workspace together")
    scope.add_argument("--list", action="store_true", help="list workspaces that have chat history, then stop")
    scope.add_argument("--chat-folder", action="append", help="a chatSessions folder to read (can repeat)")
    parser.add_argument("--since", help="sessions created on or after this date (yyyy-mm-dd)")
    parser.add_argument("--until", help="sessions created on or before this date (yyyy-mm-dd)")
    parser.add_argument("--out", default=os.path.join(tempfile.gettempdir(), "copilot-usage"), help="output folder")
    parser.add_argument("--include-messages", action="store_true",
                        help="add session titles and the first 160 characters of each message to the output (private: keep it out of git)")
    args = parser.parse_args()

    if args.list:
        found = False
        for edition, label, _, chats in workspaces():
            count = len([f for f in os.listdir(chats) if f.endswith((".jsonl", ".json"))])
            if count:
                found = True
                print(f"{count:>5} sessions  [{edition}]  {label}")
        if not found:
            print("No Copilot Chat history found.")
        return

    if args.chat_folder:
        folders = args.chat_folder
    elif args.all:
        folders = [chats for _, _, _, chats in workspaces()]
    else:
        target = os.path.normcase(os.path.abspath(args.repo or "."))
        folders = [chats for _, _, path, chats in workspaces() if path == target]
    if not folders:
        sys.exit("No chat history found for this folder. Run with --list to see the workspaces that have it.")

    since, until = parse_date(args.since), parse_date(args.until)
    os.makedirs(args.out, exist_ok=True)

    sessions, requests = [], []
    tools, models, attached = Counter(), Counter(), Counter()
    category_pct = defaultdict(list)

    for folder in folders:
        for name in sorted(os.listdir(folder)):
            if not name.endswith((".jsonl", ".json")):
                continue
            s = load_session(os.path.join(folder, name))
            reqs = s.get("requests") or []
            stamp = s.get("creationDate") or 0
            created = datetime.fromtimestamp(stamp / 1000, tz=timezone.utc)
            if not reqs or (since and created.date() < since) or (until and created.date() > until):
                continue
            session_id = os.path.splitext(name)[0][:8]
            totals = {"p": 0, "c": 0, "cr": 0.0}
            for idx, r in enumerate(reqs):
                if not isinstance(r, dict):
                    continue
                mode_info = r.get("modeInfo") or {}
                mode = mode_info.get("telemetryModeName") or mode_info.get("kind") or ""
                if mode_info and not mode_info.get("isBuiltin", True):
                    mode = "custom:" + str(mode_info.get("modeName") or mode_info.get("modeId") or "?")
                details = {d.get("label"): d.get("percentageOfPrompt", 0) for d in (r.get("promptTokenDetails") or [])}
                for label, pct in details.items():
                    category_pct[label].append(pct)
                for f in {v.get("name", "") for v in ((r.get("variableData") or {}).get("variables") or [])
                          if v.get("kind") == "promptFile"}:
                    attached[f] += 1
                calls = list(tool_calls(r.get("response")))
                tools.update(calls)
                model = r.get("modelId") or ""
                models[model] += 1
                pt = r.get("promptTokens") or 0
                ct = r.get("completionTokens") or 0
                cr = r.get("copilotCredits") or 0.0
                totals["p"] += pt
                totals["c"] += ct
                totals["cr"] += cr
                row = {
                    "session": session_id, "created": f"{created:%Y-%m-%d}", "idx": idx, "model": model, "mode": mode,
                    "promptTokens": pt, "completionTokens": ct, "credits": round(cr, 3), "toolCalls": len(calls),
                    "sysPct": details.get("System Instructions", ""), "toolDefPct": details.get("Tool Definitions", ""),
                }
                if args.include_messages:
                    row["message"] = ((r.get("message") or {}).get("text") or "").strip()[:160].replace("\n", " ")
                requests.append(row)
            sessions.append({
                "session": session_id, "created": f"{created:%Y-%m-%d}",
                "title": (s.get("customTitle") or "") if args.include_messages else "",
                "requests": len(reqs), "promptTokens": totals["p"], "completionTokens": totals["c"],
                "credits": round(totals["cr"], 2),
            })

    if not requests:
        sys.exit("No sessions in the selected date range.")

    for file_name, rows in (("sessions.csv", sessions), ("requests.csv", requests)):
        with open(os.path.join(args.out, file_name), "w", newline="", encoding="utf-8") as fh:
            writer = csv.DictWriter(fh, fieldnames=list(rows[0].keys()))
            writer.writeheader()
            writer.writerows(rows)

    total_credits = sum(r["credits"] for r in requests) or 1
    prompts = [r["promptTokens"] for r in requests if r["promptTokens"]]
    credits = [r["credits"] for r in requests if r["credits"]]
    firsts = [r for r in requests if r["idx"] == 0 and r["promptTokens"] and r["sysPct"] != ""]

    lines = [
        f"date range: {min(s['created'] for s in sessions)} to {max(s['created'] for s in sessions)}",
        f"sessions: {len(sessions)}   requests: {len(requests)}",
        f"prompt tokens: total {sum(prompts):,}   median per request {int(median(prompts)):,}",
        f"credits (as recorded by VS Code): total {sum(credits):,.1f}   median per request {median(credits):.2f}",
        f"requests per session: median {median([s['requests'] for s in sessions])}   max {max(s['requests'] for s in sessions)}",
    ]
    if firsts:
        lines.append(
            f"first request of a session (recurring context): median prompt "
            f"{int(median([r['promptTokens'] for r in firsts])):,} tokens, instructions ~"
            f"{int(median([r['promptTokens'] * float(r['sysPct']) / 100 for r in firsts])):,}, tool definitions ~"
            f"{int(median([r['promptTokens'] * float(r['toolDefPct'] or 0) / 100 for r in firsts])):,}")
    lines += ["", "share of credits by position in the session:"]
    for lo, hi in ((0, 4), (5, 19), (20, 99), (100, 10 ** 9)):
        xs = [r for r in requests if lo <= r["idx"] <= hi]
        if xs:
            span = f"{lo + 1}-{hi + 1}" if hi < 10 ** 9 else f"after {lo}"
            lines.append(f"  requests {span:<10} {len(xs):>6} requests, median prompt "
                         f"{int(median([x['promptTokens'] for x in xs])):>9,}, "
                         f"{100 * sum(x['credits'] for x in xs) / total_credits:5.1f}% of credits")
    if category_pct:
        lines += ["", "average share of the prompt:"]
        lines += [f"  {k}: {statistics.mean(v):.0f}%"
                  for k, v in sorted(category_pct.items(), key=lambda x: -statistics.mean(x[1]))]
    lines += ["", "models (requests, median credits per request):"]
    for model, count in models.most_common():
        lines.append(f"  {model or '?'}: {count}, "
                     f"{median([r['credits'] for r in requests if r['model'] == model]):.2f}")
    lines += ["", "modes and agents (requests):"]
    lines += [f"  {k or '(not recorded)'}: {v}" for k, v in Counter(r["mode"] for r in requests).most_common()]
    if attached:
        lines += ["", "instruction and skill files attached (requests):"]
        lines += [f"  {k}: {v}" for k, v in attached.most_common(25)]
    if tools:
        lines += ["", "top tools (calls):"] + [f"  {k}: {v}" for k, v in tools.most_common(20)]
    lines += ["", "top 10 sessions by credits:"]
    lines += [f"  {s['created']}  {s['session']}  {s['credits']:>9,.1f} credits  {s['requests']:>5} requests"
              + (f"  {s['title'][:50]}" if s["title"] else "")
              for s in sorted(sessions, key=lambda x: -x["credits"])[:10]]

    summary = "\n".join(lines)
    with open(os.path.join(args.out, "summary.txt"), "w", encoding="utf-8") as fh:
        fh.write(summary + "\n")
    print(summary)
    print(f"\nFiles written to {args.out}")


if __name__ == "__main__":
    main()
