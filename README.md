# copilot-usage

See where your GitHub Copilot Chat tokens actually go, from the history VS Code already keeps on
your disk.

One Python file, no packages to install, read-only. It works on Windows, macOS and Linux, with
VS Code, VS Code Insiders, VSCodium and Code - OSS.

## Why

Each Copilot request is built from the context available to it: instructions, tool definitions,
the chat history and tool results. Some of that recurs on every request, and some of it grows as a
session goes on. This script measures both, from your own data, so you can see which one to fix.

## Requirements

- Python 3.8 or later
- VS Code with GitHub Copilot Chat, and some chat history

## Run it

```bash
# the workspace for the folder you are in
python copilot_usage.py

# the workspace for another folder
python copilot_usage.py --repo ~/src/my-app

# every workspace that has chat history
python copilot_usage.py --list

# all workspaces together
python copilot_usage.py --all

# one period, for a before-and-after comparison
python copilot_usage.py --since 2026-10-01 --until 2026-10-31
```

The summary prints to the screen. `summary.txt`, `sessions.csv` and `requests.csv` go to a
`copilot-usage` folder in your temp directory (change it with `--out`).

To check the parser without your own history:

```bash
python copilot_usage.py --chat-folder sample/chatSessions
```

## What the summary tells you

| Line | What it means | What to do if it is high |
|---|---|---|
| First request of a session | Close to pure recurring context: instructions and tool definitions, paid before your question is read | Fewer always-on instructions, fewer enabled tools and MCP servers |
| Average share of the prompt | What fills the context. Instructions and tool definitions recur; messages and tool results grow with the session | Fix whichever half is bigger |
| Share of credits by position in the session | How much of your cost comes from late turns in long sessions | Start a new session when the task changes; carry a short written summary, not the history |
| Top sessions by credits | The outliers | Look at why they ran so long |
| Models | Median credits per request for each model | Use the cheaper model for routine edits, builds and tests |
| Modes and agents | How often each built-in mode or custom agent is used | Remove agents nobody uses |
| Instruction and skill files attached | Which files actually went with each request | Check that "scoped" files are not attached everywhere |
| Top tools | What the agent does most | Move repeated multi-step procedures into scripts |

## Where the data is

| OS | Folder |
|---|---|
| Windows | `%APPDATA%\Code\User\workspaceStorage\<id>\chatSessions\` |
| macOS | `~/Library/Application Support/Code/User/workspaceStorage/<id>/chatSessions/` |
| Linux | `~/.config/Code/User/workspaceStorage/<id>/chatSessions/` |

Other editions use `Code - Insiders`, `VSCodium` or `Code - OSS` in place of `Code`. A
`workspace.json` next to each `chatSessions` folder names the project. Chats from windows with no
folder open are in `globalStorage/emptyWindowChatSessions/` (included with `--all`).

Newer VS Code versions write each session as an operation log (`.jsonl`): the first line is the
full state, and each later line sets (`kind: 1`), appends (`kind: 2`) or deletes (`kind: 3`) a value
at a path. Older versions write one JSON document (`.json`). The script reads both.

## Privacy

- The script only reads. It sends nothing anywhere.
- By default, the output has **no message text and no session titles**. `--include-messages`
  adds session titles and the first 160 characters of each message. Keep that output private and
  out of git.

## Limits

- This reads internal VS Code storage, not a documented API. Any VS Code release can change it.
- `copilotCredits` is the value VS Code records locally. It can differ from your bill.
- Requests that were cancelled or failed may have no token data, and count as 0.
- The data covers one machine and one VS Code profile.
- Near the context limit, VS Code can compact older turns into a summary, so long sessions do not
  grow without limit. The numbers show what was actually sent.

## Licence

MIT. Free to use, change and share. See [LICENSE](LICENSE).
