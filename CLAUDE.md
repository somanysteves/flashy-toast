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
- changes to repo admin settings (branch protection, secrets, workflow
  permissions, collaborators) — these affect shared infrastructure even
  when the user has stated the goal

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

## Dev flow: feature branches and PRs

Non-trivial work goes on a feature branch and merges via PR — `main` is
not a working branch.

1. Branch: `git checkout -b <name>`. Don't commit on `main`.
2. Commit on the feature branch (each `git commit` still needs explicit
   approval, per the rule above).
3. Push the branch and open the PR with `gh pr create`. Both actions are
   remote-affecting and need their own explicit approval.
4. Wait for the `build` workflow to finish on the PR. Don't suggest the
   user merge while checks are pending or red — read the result first
   with `gh pr checks` / `gh run view`.
5. If `main` has moved since the PR branched, merge `main` into the
   feature branch (`git merge main` from the feature branch, or click
   "Update branch" on the PR page) and push normally. Branch protection
   requires PRs be up-to-date with `main` before merge (the `strict`
   required-status-check setting). Don't rebase + force-push — we want
   the feature-branch commits preserved as-is, and force-push is on the
   explicit-approval list above for good reason.
6. The user merges, using a merge commit (not squash, not rebase). The
   feature branch's commits are intentionally part of `main`'s history.
   Claude does NOT run `gh pr merge` or click merge on the user's behalf,
   even after CI is green.

`main` is branch-protected: the `build` check is a required status check,
so merges (and direct pushes) need a green build on the head SHA. If a
push or merge gets rejected by branch protection, don't look for a
workaround — that's the system enforcing this rule, same as the
`git push` approval hook.

Trivial fixes (typos, README) still go through this flow. There's no
"small enough to skip the PR" carve-out.
