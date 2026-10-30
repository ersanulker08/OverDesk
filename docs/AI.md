# OverDesk's AI

The AI panel is more than a chat. The model gets **tools** for everything under OverDesk's roof, and every
provider, whether a local model, a cloud API or an AI app installed on the computer, gets the same ones.

## The pieces

```
src/Overdesk.AI/
  ChatProvider.cs        IChatProvider (+ ISessionChatProvider for apps that keep their own session), ChatChunk
  AiTools.cs             AiTool (JSON Schema + handler + chat label + optional approval), AiToolbox, AiContext
  ClaudeProvider.cs      Anthropic API: streamed tool loop, thinking blocks sent back with their signatures, prompt caching
  OpenAiCompatibleProvider.cs   OpenAI and LM Studio: streamed tool_calls
  OllamaProvider.cs      Ollama /api/chat: tool_calls in NDJSON, 16K context when tools are on
  GeminiProvider.cs      Gemini API: functionDeclarations, model parts (thought signatures) sent back unchanged
  ClaudeCodeProvider.cs  The user's Claude Code (`claude -p --output-format stream-json`), OverDesk tools over MCP
  GeminiCliProvider.cs   The user's Gemini CLI, OverDesk tools over MCP (.gemini/settings.json in its workspace)
  McpServer.cs           Model Context Protocol server on 127.0.0.1 (HTTP over a socket, bearer token, no browsers)
  ProjectTools.cs        The project folder: overview for the prompt, list/read/search/write/edit tools
  AiMemory.cs            Notes the AI keeps between chats (about the user, per project)
src/Overdesk.App/
  Services/AiService.cs  Conversations, the system prompt, building the tools for a turn, approvals, streaming
  MainWindow.AiTools.cs  OverDesk tools: tabs, programs, sites, panel tools, screen, clipboard, editor, works
  MainWindow.AiDesk.cs   Tasks, dev servers, media, system, scratchpad, reminders, focus timer, projects,
                         workspaces, split view, web pages, scripting other programs
  AiPanelView.axaml(.cs) The panel: Markdown answers, action lines, approval cards, folder chip, drops
```

## One turn

1. `AiService.SendAsync` builds the tools for this turn: OverDesk's own (when *Let the AI work inside OverDesk* is on),
   the memory tool, the project folder's file tools (when a folder is chosen), and tools offered by plugins.
   Tools switched off in **Settings → AI → What the AI may do** are left out.
2. The system prompt is the user's instructions + what the AI remembers + a line about what is on stage + (with a folder)
   the project overview (layout, build files, README / CLAUDE.md / AGENTS.md).
3. The provider streams the answer. When the model calls a tool, `AiToolbox.RunAsync` runs it: it asks for approval
   first when the tool needs it, reports a chat line ("Editing src/App.cs"), and turns exceptions into error results
   the model can react to. API providers loop (up to 30 rounds) until the model stops calling tools.
4. Apps that run as their own process cannot be handed C# objects, so `McpServer` serves the same toolbox. Claude Code
   gets `--mcp-config` (with a random token) and `--allowedTools mcp__overdesk`; it runs inside the project folder with
   its own Read/Grep/Glob, Edit/Write only when file changes are allowed, and never its own shell (it uses
   `run_command`, which asks first).

## Tools

| Area | Tools |
|---|---|
| OverDesk | `overdesk_state`, `open_program`, `switch_tab`, `close_tab`*, `open_website`, `open_tool`, `workspace`, `split_view` |
| Seeing | `look_at_screen`, `read_web_page`, `read_clipboard`, `clipboard_history`, `system_status` |
| Works | `list_works`, `read_work`, `create_work`, `add_work_rows`, `update_work_row`, `delete_work_rows`, `update_work`, `delete_work`*, `open_work` |
| Project folder | `list_files`, `read_file`, `search_files`, `write_file`, `edit_file`, `open_in_editor`, `run_command`* |
| Desk | `read_tasks`, `dev_servers`, `media`, `scratchpad`, `remind_me`, `focus_timer`, `projects` (launch*) |
| Other programs | `automate_app`* (AppleScript on macOS, PowerShell on Windows) |
| Memory | `memory` |
| Plugins | anything a plugin offers: the Converter's `convert_files`, DevKit's `devkit` (exact hashes, encoders, generators, regex, diff, JWT, cron, colors, JSON to types) and `hash_file`, Texture Maps' `make_texture_maps`, App Icons' `make_app_icons`, Sprite Sheet's `pack_sprites` and `slice_sprite_sheet` |

\* asks the user first (Allow / Allow all in this chat / Deny). Stopping the chat counts as Deny.

## Safety rules

- File tools never leave the chosen folder (symbolic links included) and never write inside `.git`.
- With *Read only*, the write tools are not offered at all, and Claude Code runs with its edit tools blocked.
- Commands, scripts for other programs, deleting a work and closing a tab always ask. A deleted work is copied to
  `works-trash` first.
- The MCP server listens on 127.0.0.1 only, needs the per-run bearer token, and refuses requests with an `Origin`
  header (web pages).
- API keys live in the system keychain; the AI never sees them.

## Adding a tool

Inside OverDesk: add an `AiTool` in one of the `MainWindow.Ai*.cs` files. Run anything that touches the UI through
`Ui(...)`, since tools are called from background threads. Give it a `Label` for the chat line and an `Approval` when
it is hard to undo.

From a plugin: implement `IAiToolSource` and set `"ai": true` in the manifest. See [PLUGINS.md](PLUGINS.md).

## Testing without spending tokens

`tests/Overdesk.Tests` drives every provider against fakes: a local server that streams canned API answers (Claude,
OpenAI-compatible, Ollama, Gemini) and a fake `claude` script for Claude Code. They check the tool loops and what is sent
back (thinking signatures, tool results, flags). For a real end-to-end check from inside the app:

```bash
OVERDESK_DATA_DIR=/tmp/od OVERDESK_SNAPSHOT=/tmp/shots OVERDESK_SNAPSHOT_AI_MODEL=haiku \
  OVERDESK_SNAPSHOT_AI_PROMPT="Create a work called Test with two to-dos" dotnet run --project src/Overdesk.App
```

It prints what the AI did. `OVERDESK_SNAPSHOT_AI_FOLDER` picks a project folder and `OVERDESK_SNAPSHOT_AI_ALLOW=1`
answers approvals with Allow. This uses your real provider (and its quota).
