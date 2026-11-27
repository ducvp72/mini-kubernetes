---
name: handoff
description: Write a short session handoff note (HANDOFF.md). Use ONLY when the user explicitly says "handoff" or "/handoff". Do NOT use for ordinary questions, debugging, or code changes.
disable-model-invocation: true
---

Write a handoff note to `HANDOFF.md` in the project root (overwrite if it exists),
so a fresh session can continue this work without the current conversation.

Rules:
- Maximum 20 lines. Shorter is better.
- Write only what a new session cannot find by itself from the code, `git diff`,
  or `CLAUDE.md` / `AGENTS.md`. Do NOT list changed files or paste code.
- Every line must pass this test: "if I dropped this line, would the next session
  make a mistake or waste time?" If not, drop it.
- If there is nothing to write under a heading, omit that heading.

Use exactly this format:

```markdown
# Handoff

Goal: <one sentence>

Decisions (with the reason):
- <decision>: <why>

Tried and dropped:
- <approach>: <why it failed>

Next step:
1. <specific, actionable step, with file name if relevant>

Gotchas / open questions:
- <trap, surprise, or question that is still unanswered>
```

After writing, show the user the note in the reply and ask them to correct
anything wrong. Remind them that HANDOFF.md should be in `.gitignore`.