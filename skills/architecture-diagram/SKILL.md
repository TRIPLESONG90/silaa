---
name: architecture-diagram
description: Analyze a codebase or system description and create evidence-based software architecture diagrams. Use for system context, container, component, deployment, data-flow, ERD, or sequence views; do not use for decorative illustrations.
---

# Architecture diagram

Create the smallest diagram that answers the user's architectural question and remains easy to update.

## Workflow

1. Inspect the relevant code, configuration, deployment files, schemas, and existing documentation before drawing. Treat names found only in prose as claims, not verified implementation.
2. Choose one primary view:
   - System context for people, external systems, and the product boundary.
   - Container for applications, services, databases, queues, and major protocols.
   - Component for internals of one selected container.
   - Deployment for processes, hosts, networks, ports, and runtime dependencies.
   - Sequence for one time-ordered interaction or failure path.
   - Data flow or ERD when data ownership and persistence are the main question.
3. Prefer Mermaid inside Markdown so the result is diffable and renderable in silaa. Use stable, readable node IDs and short visible labels.
4. Keep the main view near 5–15 nodes. Split a dense diagram into overview and focused views instead of shrinking text or crossing many edges.
5. Label important edges with protocol, direction, event, or data type. Show trust or process boundaries only when supported by evidence.
6. Separate verified structure from inference. Put uncertain elements in a visibly dashed group or list them below the diagram as `확인 필요`; never invent ports, databases, services, or security controls.
7. Add a compact legend and 3–7 observations only when they help interpret the diagram. Link observations to inspected files where possible.
8. Before finishing, check that every node has a purpose, every edge has a direction, acronyms are explained, and the Mermaid block has balanced syntax.

For Mermaid patterns and view-selection constraints, read [references/mermaid-views.md](references/mermaid-views.md) only when authoring or repairing the diagram.

## Output placement

- In chat, return the Mermaid block plus a concise explanation.
- When the user asks to save it, prefer `docs/architecture/<descriptive-name>.md` unless the repository already has an architecture-document convention.
- Preserve existing architecture documents unless the user explicitly asks to replace them.
