# AGENTS.md — delaney

> Five-line orientation for any AI coding agent (Claude Code, Codex, Cursor, Cline, Aider, etc.) entering this repo. Rendered from the cowork kernel's `_cowork/templates/_cowork-AGENTS.template.md`; canonical methodology lives in the kernel. Don't hand-edit this file — edit the template (or, if the project has one, its `vars/delaney.json` in the kernel — vars-less projects render from the generic scaffold) and re-render with `render-agents.py`. Re-stamp provenance lives in `git log --follow`.

**Start here:** read the kernel-side `_cowork/dashboard-delaney.json` for the current WBS (kernel-root-relative per KM293; this repo's `_cowork/` does NOT carry a dashboard.json). Owner adds a `PROJECT_STATUS.md` at this repo root if/when the project warrants one.

**How we work:** this project follows **Draft-First Cowork** (artifact-first, mechanical validation, run-log over narrative, License to Execute). The canonical working agreement is in the cowork kernel at `../Cowork Project Prompt Enginner/_cowork/working-agreement.md`. The named-pattern lexicon (Spec Spiral, Subtree Cowork, etc.) is in the kernel's `pattern-catalogue.md`. When in doubt, don't pre-spec — draft the artifact, run the gate, log the result.

**Council artefacts:** if you encounter a `_council/knowledge/` tree in this repo (multi-model arbitration: append-only `transactions/<run_id>/` write-model + evolved `conclusion.json` read-model; do NOT hand-edit), the outside-facing explainer is at `../Cowork Project Prompt Enginner/_cowork/_council/README.md` and the canonical schemas at `.../CONVENTION-council.md`. The tool is `plugin/tools/council.py`.

## Cowork session mount discipline

When starting any Cowork chat task for this project, attach BOTH folders:

- `C:\CC\delaney` (this repo)
- `C:\CC\Cowork Project Prompt Enginner` (the kernel)

Or simpler: attach `C:\CC\` (parent) — exposes kernel + every project at once.

Why: kernel canonical state (`_cowork/PROJECT-MAP.json`, `plugin/tools/`, `plugin/commands/`, `_cowork/hooks/`, the kernel-side `_cowork/dashboard-delaney.json`) lives in the kernel folder. Every kernel preset reads `PROJECT-MAP.json` during its Step-1 handshake and invokes kernel tools by path from the kernel root — `python plugin/tools/X.py` — because in-session invocation resolves only the full path form, never the tool name alone (WA §5 rail 28). A single-mount task cannot reach that canonical kernel state, which is why both mounts are attached.

See `../Cowork Project Prompt Enginner/_cowork/COWORK-SESSION-SETUP.md` for the canonical multi-mount setup recipe.

**Per-batch convention:** every non-trivial batch lands a run-log at the kernel `_cowork/runs/<UTC-stamp>-DM<n>.log` ending with a single `[END] outcome=…` line. The kernel-side `_cowork/dashboard-delaney.json` is the WBS source of truth (kernel-root-relative per KM293; any in-repo `_cowork/dashboard.*` is a do-not-hand-edit rendered sidecar) — bump the relevant node's `last` and `status` via `python plugin/tools/tfi-edit.py --rewrite` run from the kernel root.

**No-touch zones:** owner-defined; none at scaffold time. Add as patterns emerge.

*Project prefix: `DM`. Mount: `C:\CC\delaney`. Rendered from the kernel AGENTS template — edit the template or vars, not this file.*

<!-- eof: agents-md-v3 -->
