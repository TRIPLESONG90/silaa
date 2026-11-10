# Native harness port map

This document records how the locally inspected OpenCode harness responsibilities were reduced and reimplemented. It is an architecture reference only; no OpenCode package or runtime is linked or shipped.

| Inspected responsibility | Native silaa implementation | Form |
|---|---|---|
| Session create/list/history | `SessionStore` | C# + local JSON |
| Prompt/run orchestration | `LlmHarness` | Microsoft Agent Framework `AIAgent` |
| Provider request | `Microsoft.Agents.AI.OpenAI` | Chat Completion provider |
| Tool registry and execution | `WorkspaceTools` | Agent Framework `AIFunction` + C# native tools |
| Instruction and skill discovery | `SkillLoader` | C# loader |
| Base agent behavior | `prompts/system.md` | bundled Markdown |
| Review, implementation, testing, debugging guidance | `skills/*/SKILL.md` | bundled Markdown |
| Run status and text events | `ChatRunEvent` | C# callback |
| Interrupt | cancellation token | .NET cancellation |

Microsoft Agent Framework sends conversation history and function schemas, invokes returned functions, feeds results back to the model, and streams `AgentResponseUpdate` instances to the UI. `LegacyMaxTokensPolicy` rewrites only the OpenAI SDK token field when a compatible endpoint requires `max_tokens`.

## Deliberately excluded

- CLI and TUI layers
- JavaScript/Bun host and SDK bridge
- HTTP agent server
- plugin and MCP hosts
- PTY and persistent terminal services
- worktree, snapshot, scheduler, voice, image, and web-search subsystems
- external engine database and migration layers

The retained tools are limited to file listing, file reading, literal search, file writing, exact text replacement, and a workspace command runner.
