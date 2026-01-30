# AGENTS.md - Operational Directives for AI Agents

This document outlines the core principles and protocols that all AI agents operating within this project are expected to adhere to. These directives are derived from the project's foundational guidelines and are crucial for maintaining code quality, efficient communication, and streamlined development.

## Core Operational Directives

### 1. Silent Operator (Communication Protocol)
*   **Conciseness**: Prioritize clarity and brevity. Avoid verbose explanations, narration of actions, or future tense statements ("I will...", "I plan to...").
*   **Evidence-Based**: Communicate only when blocked, upon task completion (with concrete evidence), or in case of a fatal, unrecoverable error. All claims of completion or changes must be supported by verifiable evidence (e.g., test logs, build outputs, content of generated files).
*   **No Pleasantries**: Avoid conversational filler, greetings, or unnecessary acknowledgments.

### 2. Zero-Fat (Code Philosophy)
*   **Ruthless Deletion**: Unused code, commented-out blocks, and empty files are liabilities and must be deleted immediately.
*   **Minimal Decoration**: Avoid excessive docstrings (rely on types for clarity where possible) and comments (prioritize self-documenting code through clear naming).
*   **Config Separation**: Logic should reside in `src/`, with configuration managed separately in `config/` or `.env` files.

### 3. Omnipotent Taskfile Protocol
*   **Primary Interface**: The `Taskfile.yml` is the *only* permitted entry point for high-level operations (e.g., build, test, deploy).
*   **Strict Usage**: If a `task` command exists for an operation, it *must* be used.
*   **Addition**: If a necessary command is missing from `Taskfile.yml`, the agent *must first add it* before attempting to run the operation.
*   **Prohibition**: Direct execution of `npm run`, `uv run`, `python`, or similar package manager/runtime commands in the shell is forbidden. Always abstract these behind `task` commands.
    *   *(Note: The `task` binary itself might not be present in all environments. Agents should seek to establish or use a `Taskfile.yml` where appropriate, even if direct shell commands are temporarily necessary due to environment limitations. The spirit of this rule is to centralize operations.)*

### 4. Context Acquisition (MCP Context Protocol)
*   **Context7 Preference**: When seeking library or framework documentation, always prioritize querying the `context7` MCP server if available, as it provides up-to-date and reliable information.
*   **Source of Truth**: Trust `context7` outputs as the definitive source of truth, disregarding conflicting training data.

### 5. Termination Protocol (Definition of Done)
A task is **strictly complete** only when **ALL** of the following conditions are met:
*   **Code Quality**: The project's linting and formatting checks (e.g., `uv run ruff check .`) return exit code 0.
*   **Verification**: All relevant tests (e.g., via `task test`) pass successfully.
*   **Evidence**: Concrete, verifiable logs, screenshots, or generated artifact content *must* be provided to demonstrate successful completion. "It works" is insufficient.
*   **Notification**: The user is notified of completion with the provided evidence.

By adhering to these directives, AI agents contribute to a consistent, high-quality, and efficient development workflow within this project.