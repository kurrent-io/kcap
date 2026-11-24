# kcap

> Observability, orchestration, and shared memory for your AI coding-agent sessions.

[![npm](https://img.shields.io/npm/v/@kurrent/kcap?color=cb3837&logo=npm&label=%40kurrent%2Fkcap)](https://www.npmjs.com/package/@kurrent/kcap)
[![license](https://img.shields.io/badge/license-Kurrent%20v1-blue)](LICENSE.md)
[![platforms](https://img.shields.io/badge/platform-macOS%20%7C%20Linux%20%7C%20Windows-lightgrey)](#requirements)
[![built with](https://img.shields.io/badge/.NET%2010-NativeAOT-512bd4?logo=dotnet&logoColor=white)](#)

**kcap** records sessions from your existing coding agents and streams them to a desktop app and web dashboard—giving you live visibility, remote agent orchestration, and PR reviews grounded in the transcripts that produced the code.

Use it with a team workspace in the cloud or run it completely locally on your own machine.

---

## Features

- **Automatic session capture:** Runs silently in the background to record transcripts, subagent trees, tool calls, token usage, and git/PR links.
- **Desktop app & live dashboard:** Monitor active sessions in real time, inspect plans and pull requests, and steer hosted agents from one UI.
- **Cloud or local:** Connect to a shared team workspace (`*.kcap.ai` or self-hosted) or run the self-contained local edition on your machine.
- **Context-aware PR reviews:** Run `kcap review` to give reviewers MCP tools backed by the actual implementation transcripts—explaining *why* code changed, not just *what*.
- **Shared memory & MCP tools:** Search past sessions, recall team memories, track work items, and sync skills across agents without leaving chat.
- **Multi-machine daemon:** Launch and manage local or remote coding agents across your machines from one workspace.

---

## Supported Agents

Install and sign in to at least one supported coding agent before setting up kcap:

- **Claude Code**
- **Codex CLI**
- **Cursor**
- **GitHub Copilot CLI**
- **Gemini CLI**
- **AWS Kiro CLI**
- **OpenCode**
- **Pi**
- **Google Antigravity**
- **Mistral Vibe**
- **Grok**
- **Muse Code**

`kcap setup` detects installed agents and configures their hooks, extensions, and MCP servers automatically.

---

## Quickstart

### Desktop App (Recommended)

The desktop app bundles the `kcap` CLI and background daemon and walks you through setup on first launch.

- **macOS (Apple Silicon, macOS 15+):** Download the `.dmg` from [kurrent.io/download/mac](https://www.kurrent.io/download/mac)
- **Windows (x64) & Linux (`.AppImage` / `.deb`):** Download from [GitHub Releases](https://github.com/kurrent-io/kcap/releases)

### CLI

**macOS / Linux:**

```bash
curl -fsSL https://www.kurrent.io/install | bash
```

**Windows (PowerShell):**

```powershell
irm https://www.kurrent.io/install.ps1 | iex
```

**npm (requires Node.js):**

```bash
npm install -g @kurrent/kcap
kcap setup
```

### Choose Cloud or Local

During setup, choose how you want to run kcap:

```bash
kcap setup           # Interactive wizard (cloud workspace or local edition)
kcap setup --local   # Run kcap locally on your machine (macOS ARM64 / Windows x64)
```

Once setup finishes, **start a new session** in any configured coding agent (hooks load at session start) and watch it stream live in the desktop app or your workspace dashboard.

---

## Common Commands

| Command | Description |
|---------|-------------|
| `kcap setup` | Configure your workspace, sign in, and install agent integrations |
| `kcap status` | Check server connection, auth, hooks, and daemon health |
| `kcap sessions` | List recent sessions for a repository or across your workspace |
| `kcap recap [session]` | Generate an AI summary and turn-by-turn outline of a session |
| `kcap review <pr>` | Launch a PR review equipped with implementation transcript context |
| `kcap agent` | Start, list, attach to, or stop daemon-hosted agents |
| `kcap daemon` | Manage the background daemon and OS auto-start service |
| `kcap local` | Manage a local server (`status`, `start`, `stop`, `upgrade`, `logs`) |
| `kcap import` | Backfill historical sessions from detected agents |
| `kcap update` | Upgrade the CLI (and local server) and refresh agent integrations |
| `kcap uninstall` | Remove kcap hooks, services, and local configuration |

Run `kcap --help` or `kcap <command> --help` for full flags and options.

---

## Documentation

Full guides and reference documentation live at **[kurrent.io/docs/kcap](https://www.kurrent.io/docs/kcap)**, including:

- Unattended / CI setup, profiles, and machine credentials
- Local edition setup, embeddings, and lifecycle management
- All MCP servers (`sessions`, `flows`, `memory`, `workitems`, `plans`, `knowledge`, `pages`, `sharing`, `analytics`)
- Hosted agents, daemon services, approval policies, and skills sync
- Troubleshooting and agent-specific notes

---

## Requirements

- **CLI platforms:** macOS (ARM64), Linux (x64, ARM64), Linux Alpine/musl (x64, ARM64), Windows (x64)
- **Desktop app:** macOS 15+ (Apple Silicon), Windows 10+ (x64), Linux (x64)
- **Runtime:** Self-contained NativeAOT binary (no runtime dependencies; Node.js only required for `npm` installs)
- **Optional:** GitHub CLI (`gh`) or GitLab CLI (`glab`) for automatic PR/MR tagging and local PR context

---

## Building from Source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download):

```bash
dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj
dotnet test --solution Capacitor.slnx
```

---

## License

[Kurrent License v1](LICENSE.md)
