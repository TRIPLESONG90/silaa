---
name: web-app-delivery
description: Build an application deliverable using silaa's default full-stack Next.js, SQLite, and thin .NET process-host baseline when the user has not selected another stack.
---

# Web app delivery

Apply this skill only when the requested output is an application. Do not turn documents, scripts, automations, or skills into web apps without a product reason.

- Use Next.js for the UI and business API through Route Handlers or Server Actions unless the project or user selected another stack.
- Let the Next.js server exclusively open, migrate, and query SQLite. Never copy, download, or serve the raw database file.
- Keep .NET thin: it only starts, supervises, stops, and reverse-proxies the Next.js child process. Do not duplicate business endpoints or database access in .NET.
- Bind the .NET host and internal Next.js process to the project ports supplied in the project-delivery instructions. Keep both overridable through environment variables or launch settings.
- Provide a single documented .NET host entry point that starts the complete application and terminates the child process cleanly.
- Default local listeners to `127.0.0.1`. Do not bind to all interfaces unless the user explicitly asks and understands the exposure.
- External access uses ngrok only when the user explicitly requests it. Tunnel the public .NET host port, never the internal Next.js port or SQLite. Request the ngrok token through Vault and inject it into the child process without printing or persisting it in project files.
- Add health checks, graceful shutdown, SQLite busy timeout/WAL settings where appropriate, and a short README with run, migration, backup, and verification steps.
- Verify the Next.js API internally, then verify the complete flow through the public .NET host URL. Report exact URLs and commands that were actually tested.
