# Working norms for Claude on flashy-toast

## NEVER ship without explicit approval

Do NOT run any of the following without an explicit go-ahead from the user
**in the current message**:

- `git commit`
- `git push` (or `git push --tags`)
- `git tag`
- `git push --force` / any history rewrite
- branch deletion (local or remote)
- `gh release create` / `gh release upload`
- any other action that publishes code, artifacts, or refs to a remote

This applies even when chaining ("push then tag then release") — each step
needs its own yes, not a single blanket yes for the chain.

A previous approval (e.g. "commit it" yesterday, "ship it" earlier in this
conversation) does NOT carry forward to a new shipping action. Every new
shipping action requires a fresh, explicit yes for *that specific action*.

"Auto mode" / continuous-execution mode does NOT relax this rule. Auto mode
is for autonomous local work — edits, builds, smoke tests in a worktree —
not for irreversible or public actions.

When the user describes a goal that involves shipping ("publish a release",
"open a PR", "tag v0.2.0"), treat it as a goal, not a green light: produce
the artifact locally, draft the message/notes, summarize what *would* run,
and wait for an explicit "do it."

If a hook denies an action, do NOT look for a workaround — that's the system
enforcing this same rule.

## Local work that's fine without asking

- Edits to source files, csproj, README, PLAN
- `dotnet build`, `dotnet publish`
- Running the exe in a smoke-test fashion and killing it
- Reading logs / git status / git diff
