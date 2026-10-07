# ExpansePlatform development ownership

User instruction updated on 2026-10-01:

- GPT-6.1 Sol owns design, implementation, testing, review, and integration. It should do the work itself by default.
- Use GPT-6 Astra only for a specific problem where Sol needs its assistance. State the reason for that escalation; do not delegate design or review to Astra automatically.
- GPT-6 Luna may perform bounded implementation or QA tasks under GPT-6.1 Sol's supervision. Sol defines the task and reviews the result before integration.
- Do not silently substitute GPT-5.6 Luna or another older model. If GPT-6 Luna is unavailable through the delegation tool, keep the work with GPT-6.1 Sol.

Keep existing file ownership coordinated while agents are active. These ownership rules do not authorize production changes or game fixtures; follow the task's explicit deployment and fixture gates.

## Simplicity

User instruction, 2026-10-04: apply KISS (keep it simple). Preserve the requested capabilities while choosing the simplest dependable implementation and clear, compact interfaces. Finish one complete working colony loop before adding general abstractions. Prefer one current build, targeted tests for actual risks, and concise progress records; avoid multiplying intermediate versions, packaging layers, repeated audits, or speculative features. Keep necessary save protection and financial/resource correctness checks.
