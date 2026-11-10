# Hermes Agent harness alignment

silaa uses Microsoft Agent Framework as its native C# runtime. It does not embed or launch Hermes Agent, but its harness design should reuse the strongest open-source patterns from the official [NousResearch/hermes-agent](https://github.com/NousResearch/hermes-agent) architecture where they fit a local Windows desktop product.

## Principles adopted by silaa

| Hermes pattern | silaa direction |
| --- | --- |
| One platform-agnostic agent core | Keep `LlmHarness` shared by chat, projects, onboarding, background work, and schedules. UI-specific behavior stays in the Avalonia layer. |
| Observable execution | Stream reasoning summaries, tool calls, results, status, and final text as ordered events. Every long operation remains cancellable. |
| Loose coupling through registries/toolsets | Compose Microsoft Agent Framework `AITool` groups by run mode and permission scope. Optional features must not become hard runtime dependencies. |
| Stable prompt prefix | Keep durable identity and security rules stable. Add project, memory, skill, and time-sensitive context in clearly separated tiers. Do not mutate the system prompt during a turn. |
| Progressive skill disclosure | Load portable `SKILL.md` guidance and read its supporting files through `read_skill_reference` only when needed instead of claiming that linked references are unavailable. |
| Session and profile isolation | Persist sessions separately, track active runs per session, and allow different sessions to call the same provider concurrently. A foreground request must not globally block unrelated chats. |
| Bounded, curated memory | Keep durable user/project facts reviewable and scoped. Do not treat the live transcript as permanent memory. |
| Background jobs as fresh agent runs | Give each scheduled/background run a self-contained instruction and a clean run context, then persist and notify its concrete result. |
| Capability boundaries rather than model micromanagement | Let the model select ordinary read/analysis tools freely. Require explicit approval only at filesystem, network, secret, destructive, or persistence boundaries. |
| Optional isolation backends | Keep local execution as the default. Add process/container isolation as a selectable execution backend rather than pretending every local command is sandboxed. |

## Current implementation map

- Agent loop and provider adaptation: `Services/LlmHarness.cs`
- Prompt and bundled skills: `prompts/system.md`, `Services/SkillLoader.cs`
- Tool composition: `WorkspaceTools` and the focused `*Tools` services
- Permission boundaries: `SecurityPolicy`, folder/network request brokers, and Vault
- Background and scheduled execution: `BackgroundAutomationService`
- Session persistence and active-run tracking: `SessionStore`, `LlmHarness`
- Interactive callbacks: native Avalonia question, permission, calendar, and Vault cards

## Next architectural extractions

1. Split prompt assembly into stable, contextual, and volatile builders so provider prefix caching remains effective.
2. Move tool metadata and availability checks into a central registry while retaining typed C# implementations.
3. Add context usage accounting and middle-history compression with visible provenance rather than silently dropping old turns.
4. Add optional isolated execution backends with the same permission contract as local execution.
5. Persist resumable run checkpoints for long background work and application restarts.

These are architecture references, not a license to copy code blindly. Any future source-level reuse must retain the upstream MIT attribution and pass silaa's Windows security and UX requirements.
