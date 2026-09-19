# Framewright — UI review

> Review of `docs/IMPLEMENTATION_HANDOFF.md` and the shipped front end, scoped to **UI only**.
> Reviewed at commit `7cfb2af` against the running app at `127.0.0.1:5179`.
> Findings below were measured in the browser at 1512×945 and 1000×500, not inferred from source alone.
>
> **Status: all eight items in "Suggested order of work" have been implemented.**
> See [§ Resolution](#resolution) at the end for what changed and the verification numbers.
> The findings are kept in their original form so the reasoning stays reviewable.

---

## 0. Summary

The handoff document is unusually good as an *engineering* brief. Its trust boundaries, manifest contract, and shipped-vs-planned table are honest and specific, and §18's UX rules are the right rules.

The problem is that §18 is 12 bullet points at the very end of a 25-section document, and the UI was built without a design system underneath it. The result is an app whose *information architecture* is strong and whose *visual and interaction craft* is not yet at the level the rest of the work implies.

One finding dominates everything else:

> **The entire application renders at 8–13px. There is no text at 14px or larger anywhere in the Shot workspace.**

That is not a matter of taste. It is below every mainstream platform baseline, and it is the single change that will most improve how professional this reads. Everything else in this document is secondary to it.

---

## Part A — Review of the handoff document

### A1. What the document gets right

Keep all of this:

- **§18's core rules are correct.** "Canvas gets the largest visual area," "one primary action per decision point," "every visible affordance must work or be visibly labeled as preview," and the plain-language job-state vocabulary (Preparing / Rendering / Ready for review / Blocked) are exactly right for a production tool.
- **§3's workflow invariants are genuinely product-shaping**, not decoration. "Ratify the still before generating video" and "one named shot slot is the review authority" are the kind of constraint that prevents variant sprawl in the UI.
- **The shipped-vs-planned table (§8)** is the most valuable section in the file. Keep enforcing it.
- **Production vocabulary throughout.** "Ratify," "authority packet," "locked constraint," "supersede" — this is the language of the domain, not of software. The UI carries it through well.

### A2. Where the document's own UX rules are contradicted by the code

These are the ones to fix, because the doc asserts them as invariants:

| §18 rule | Actual state |
|---|---|
| "Use at least 44px core touch targets on tablet" | 44px is applied **only below 860px viewport width**. An iPad Pro 11" in landscape is 1194px and an iPad Air is 1180px — both land in the **desktop** layout with 28–32px targets. The rule is keyed to width, not to pointer type. There are **zero** `@media (pointer: coarse)` rules in the stylesheet. |
| "Every visible affordance must work or be visibly labeled as preview/coming next" | Several affordances silently do nothing (see B5). The Draw tool in the Shot workspace produces annotations that are **permanently destroyed** on navigation with no warning, undo, or save. |
| "Preserve keyboard access, visible focus, ARIA state" | The global focus ring (`--accent-strong`) sits at **1.08:1** against the sketch paper background — the focus indicator is effectively invisible in the Sketch workspace. Modal dialogs declare `aria-modal="true"` but leave 27 background elements tabbable. |
| "Destructive operations are recoverable or require confirmation" | Clear-sketch *is* recoverable via history, which is good — but nothing in the UI tells the user that, so it reads as destructive. Shot-workspace markup is genuinely unrecoverable. |

### A3. What §18 is missing entirely

The document has **no design-system section**. There is no statement anywhere of:

- a type scale, or a minimum font size
- a spacing scale
- what the color tokens *mean* (which is why `--accent` and `--authority` ended up perceptually identical)
- component states (hover / active / focus / disabled / loading / error / empty)
- density modes, or how desktop density differs from tablet density

This is the root cause of most of Part B. A tool that a professional stares at for eight hours needs its type and color decided once, centrally, and then obeyed. Right now those decisions are made ad hoc in 154 places.

**Recommendation:** add a `§18a Design system` section that is normative, and treat it the way §11's manifest contract is treated — as a contract, not a suggestion.

### A4. The verification baseline (§22) overclaims

> "`axe-core` reports no serious or critical violations on covered flows."

This is true and it is a weak signal. Automated tooling catches roughly a third of WCAG issues, and specifically **cannot** catch any of the following, all of which are present:

- font sizes that are technically legal but practically unreadable
- missing focus containment in a dialog that declares `aria-modal="true"`
- a custom combobox whose active option is never announced
- a focus ring that is invisible against one particular background
- an interactive control that does nothing when activated

**Recommendation:** reword §22 to say what the gate actually proves, and add a manual checklist (keyboard-only pass, screen-reader pass on one flow, 200% zoom, one real tablet with a stylus). Otherwise the next AI instance will read "no violations" as "accessible" and stop looking.

---

## Part B — Measured findings in the shipped UI

Ranked by impact.

### B1. Typography — the dominant issue 🔴

Measured in the running app on the Shot workspace at 1512×945:

| Rendered size | Count | Examples |
|---|---|---|
| 8px | 12 | `Approved source`, `Current · v6`, `Not started`, `STUDIO` |
| 9px | 18 | `Board`, `Shot`, `Review`, `Sequence` (the primary nav) |
| 10px | 12 | `Aerie approach` (the shot title), `Intent`, `Local studio` |
| 11px | 10 | `SH-010`, `Shot`, `Find shot or view` |
| 13px | 1 | `The Aerie Sequence` |

**The largest text on the screen is 13px.** The primary navigation is 9px. Shot titles are 10px.

Across the stylesheet:

- **167 font-size declarations. 165 are `px`. Zero `rem`, zero `em`.**
- Of the explicit `font-size: Npx` rules: **62% are ≤9px**, **81% are ≤11px**. There are three declarations at **7px**.

Two consequences:

1. **Legibility.** Apple HIG uses 13pt as its *smallest* standard macOS control size and 44×44pt touch targets; Material Design's smallest body style is 12sp. Dense professional tools (Resolve, Premiere, Figma) live at 11–13px for *secondary* chrome, not for their primary navigation and content titles. 8–9px is roughly half the size of accepted body text and is below anything shipped commercially.
2. **User font-size preference is ignored.** Because every size is `px`, a user who has set a larger default font size in their browser or OS gets no change at all. Page zoom still works, so this isn't a hard WCAG 1.4.4 failure — but it means the app is unusable-as-shipped for anyone who relies on that setting, and they have to zoom the whole layout instead.

**Fix — define a type scale and move to `rem`:**

```css
:root {
  /* 1rem = 16px browser default; scale respects user preference */
  --text-2xs: 0.6875rem; /* 11px — dense chrome, timecode, badges. FLOOR. */
  --text-xs:  0.75rem;   /* 12px — secondary labels, captions */
  --text-sm:  0.8125rem; /* 13px — body copy, list items, inspector fields */
  --text-md:  0.875rem;  /* 14px — default UI text, nav, buttons */
  --text-lg:  1rem;      /* 16px — panel titles */
  --text-xl:  1.25rem;   /* 20px — workspace headings */
  --text-2xl: 2rem;      /* 32px — board display heading */
}
```

Then: **nothing below `--text-2xs` (11px), and nothing at all at 7–10px.** The migration is mostly mechanical — 7/8px → 11px, 9/10px → 12–13px, 11px → 13–14px. Expect the layout to need real spacing adjustments afterward; that is the work, and it is worth it.

> A useful sanity check: sit at arm's length from the monitor. If you lean in to read a label, it's too small.

### B2. Color: 154 unstructured hex values, and two tokens that collide 🔴

- **18 tokens defined in `:root`. 154 unique raw hex values across the file.** Roughly 136 colors live entirely outside the token system (`#191d1b`, `#141715`, `#0e1110`, `#121513`, `#242a27`, …). There are 315 `var(--…)` usages, so the system is *half* adopted — which is the worst of both worlds, because a retheme now means auditing every rule.

- **`--accent` (#a7c7be) and `--authority` (#9bc4a8) have a 1.06:1 contrast ratio against each other.** They are perceptually the same color. These carry the two most semantically loaded meanings in the entire product — *"this is the primary action"* and *"this is approved, immutable canon"*. A user cannot distinguish an actionable control from a ratified-authority marker by color. Given that the whole product thesis is approval gates, these must be visibly different hues.

- **Non-text contrast failures (SC 1.4.11, needs 3:1):**

| Element | Ratio | Note |
|---|---|---|
| Textarea border (`--line`) vs panel | **1.36:1** | Form field boundaries are effectively invisible |
| Textarea fill vs panel | **1.06:1** | The alternate boundary route also fails — the field has no perceptible edge |
| Focus ring vs sketch paper | **1.08:1** | **Focus indicator is invisible in the Sketch workspace** |

- **Text contrast is otherwise good** — most pairs land 5:1–9:1, which is comfortably AA. The one text failure is `.sketch-stage-meta` at **2.55:1** on the paper background.

- **No light theme.** Zero `prefers-color-scheme` rules; `color-scheme: dark` is hardcoded. Dark-only is a *defensible* choice for a frame-review tool — Resolve and Premiere do it, and you want the UI to disappear around the image. But NN/g's position is that dark mode is better offered as a *choice*, since light mode outperforms for most normally-sighted users. Worth an explicit decision recorded in the doc rather than an accident of implementation.

- **No `forced-colors` support.** This is a Windows-first application; Windows High Contrast mode will produce an unpredictable result.

**Fix:** finish the token system (every color goes through a token, semantic names not literal ones), split `--accent` and `--authority` into clearly different hues, and raise `--line` to ≥3:1 wherever it forms a control boundary.

### B3. Tablet density is keyed to viewport width, not pointer type 🟠

Already covered in A2 — restating because it has a clean fix:

```css
/* Applies to any coarse pointer at any width — iPad landscape, Surface, tablets */
@media (pointer: coarse) {
  button, [role="button"], a, input, textarea, select {
    min-height: 44px;
    min-block-size: 44px;
  }
}
```

Measured on desktop layout: **15 of 29 interactive controls are under 44px**; the canvas tool buttons are **32×28**. They clear WCAG 2.2 SC 2.5.8's 24×24 floor, so this is not a conformance failure — but 24×24 is a floor, not a target, and Apple (44×44pt) and Material (48×48dp) both sit far above it. For a stylus-driven storyboard tool this is the difference between fluid and fiddly.

Note also that pointer-coarse and small-viewport are different problems: a 1194px iPad needs *big targets* but not the *stacked mobile layout*. Handle them independently.

### B4. Dialog focus management 🟠

Measured with the command palette open:

- `role="dialog"` + `aria-modal="true"` is set correctly, **but 27 focusable elements behind the dialog remain tabbable** and `.app-shell` is neither `inert` nor `aria-hidden`. Tab walks straight out of the modal into the page behind it, which for a screen-reader user means the dialog silently stops existing.
- **No focus restoration.** Closing any dialog drops focus to `<body>`; the user's place in the page is lost.
- **The command palette's arrow-key navigation is visual only.** The input has no `role="combobox"`, no `aria-expanded`, no `aria-activedescendant`; the results container has no `role="listbox"`; the options have no `id`s. Pressing ↑/↓ moves a visible highlight and announces nothing.
- `SetupDrawer` puts `autoFocus` on its **close button**, so the first thing announced on open is "Close production setup."

**Fix:** the cleanest path is the native `<dialog>` element with `showModal()` — it handles focus containment, Escape, the top layer, and background inertness for free, and per current guidance makes manual focus-trap code unnecessary. Then add `aria-activedescendant` + `role="listbox"`/`role="option"` to the palette, and restore focus to the invoking element on close.

### B5. Affordances that don't do anything 🟠

This is the doc's own rule, so these are worth listing precisely:

| Control | Behavior |
|---|---|
| **Draw tool** (Shot workspace) | Strokes render to a bare canvas that is never saved, never sent, has no undo, and is **silently destroyed on navigation**. Verified: drew on SH-010, navigated away and back, ink gone with no prompt. |
| **Version rail stages** (Draft / Final / Video) | All three call `setCanvasMode('frame')` — clicking any of them does the same nothing. |
| **`rail-history` chips** (`v5`, `v4`) | Rendered as non-interactive `<i>` elements. They look like version history and are decoration. |
| **Zoom readout** `Fit · 64%` | Hardcoded string. There is no zoom control and no zoom state. |
| **Audio clips** (Dialogue / Voices / Music) | Fixed `left`/`width` percentages, no interaction, no ARIA. Pure illustration inside an otherwise-live timeline. |
| **"Needs work" button** (Review) | Labeled as a state change; actually calls `onGenerate('Draft')` and **queues a render**. This is the most dangerous one — the label and the action disagree. |

`.new-shot-card` and `.new-authority-card` are styled in CSS but never rendered — and correspondingly, **there is no way to create a shot or an authority anywhere in the UI.** For an artist-facing tool, the absence of any creation path is a notable IA gap.

**Fix:** either wire them, or apply the doc's own rule — disable them with a visible "Coming next" affordance. A `Preview` badge costs nothing and preserves the credibility the rest of the app has earned.

### B6. Missing states 🟡

The app has one loading state (full-screen spinner) and one empty state (no open feedback). It is missing:

- **Skeletons** for the board — the full-screen spinner on every load makes the app feel slower than it is
- **Job states `Blocked` and `Failed`** — §18 defines the vocabulary, but `JobsDock` only renders `Queued`/`Running`, so a failed job just vanishes
- **Empty states** for zero shots, zero authorities, zero versions
- **Per-shot error state** — errors surface only as a global toast

Also: `refresh()` polls the **entire snapshot every 700ms** while a job runs and replaces all state wholesale. That is aggressive, and wholesale replacement during interaction is how focus loss and input jitter get introduced. Consider polling just the job, and reconciling.

### B7. Smaller items 🟡

- **`sessionStorage` for sketches is a data-loss trap.** Close the tab and the drawing is gone. Use `localStorage` (or IndexedDB for stroke volume) and show a "Saved locally" indicator — the artist needs to know their work survived.
- **Incomplete ARIA tab pattern.** `role="tablist"`/`role="tab"` are used in three places with no `role="tabpanel"`, no `aria-controls` on the inspector tabs, and no arrow-key roving tabindex. The Review mode switcher is really a radio group, not a tablist.
- **`role="toolbar"` without arrow-key navigation.** The APG toolbar pattern expects one tab stop plus arrow keys.
- **Focus triggers state change.** `.shot-card` has `onFocus={() => onSelect(shot.id)}`, so simply tabbing across the board mutates the selected shot. Select on click/Enter, not on focus.
- **`const document = history[historyIndex]`** in `SketchWorkspace.tsx:46` shadows the global `document`. It works today and it is a landmine.
- **The wipe slider** handles ←/→ but not Home/End/PageUp/PageDown, and has no `aria-valuetext`.
- **Toast auto-dismisses at 3.2s.** Fine for confirmations; make sure nothing actionable ever lands there.

---

## Part C — Recommended edits to the handoff document

1. **Add `§18a Design system` as a normative section** — type scale, spacing scale, semantic color tokens, component states, density modes. Make it as binding as §11's manifest contract.
2. **Rewrite the touch-target rule** from "44px on tablet" to "44px minimum under `@media (pointer: coarse)`, independent of viewport width."
3. **Add a rule: "No text below 11px. Body and navigation text is 13–14px."**
4. **Add a rule: "`--accent` and `--authority` must remain visually distinguishable"** — with the required minimum stated, since this one already regressed.
5. **Reword §22** to state what the automated gate proves and add the manual checklist (keyboard-only, screen reader, 200% zoom, one real tablet with a stylus).
6. **Add an entry to the §8 shipped-vs-planned table for the design system itself**, currently at "prototype-only."
7. **Add "Shot and authority creation" to §8** — it is currently absent from both the UI and the table.
8. **Add a Phase 0.5 to §19:** *UI foundation — type scale, tokens, focus management, honest affordance labeling — before provider wiring.* This is the right time to do it. Type and color changes ripple through every component, and they get dramatically more expensive once there is real data, real job states, and real error paths flowing through those components.

---

## Suggested order of work

| # | Change | Effort | Payoff |
|---|---|---|---|
| 1 | Type scale in `rem`; raise everything ≤10px | M | Largest single visual improvement |
| 2 | Split `--accent` / `--authority`; finish the token system | M | Fixes the core semantic collision |
| 3 | `@media (pointer: coarse)` targets | S | Fixes the actual target device |
| 4 | Focus ring visible on light surfaces; `--line` to ≥3:1 | S | Two clear a11y failures |
| 5 | Native `<dialog>`; restore focus on close | M | Fixes all four dialogs at once |
| 6 | Label or disable non-working affordances | S | Restores credibility; enforces the doc's own rule |
| 7 | `localStorage` for sketches + save indicator | S | Removes a data-loss trap |
| 8 | Blocked/Failed job states, skeletons, empty states | M | Completes the state model |

Items 1–4 are the ones that change how the product *reads*. Do them before wiring anything.

---

## Resolution

All eight items are implemented. Measured on the production build at `127.0.0.1:5179`.

| # | Change | Evidence |
|---|---|---|
| 1 | **Type scale.** 8 tokens on `:root`, `rem`-based. 168 declarations migrated. | **0** `px` font-sizes remain. Smallest rendered text is **11px** on every workspace (was 8px). Nav is 13px, body 13px. |
| 2 | **Colour system.** 154 raw hex values replaced by ~60 semantic tokens. | **0** raw hex outside `:root`. `--accent` → teal 195°, `--authority` → green 136°: **1.6:1** apart normally, **1.7:1** under deuteranopia and **1.67:1** under protanopia (was 1.06:1, indistinguishable). |
| 3 | **Touch targets.** Moved to `@media (pointer: coarse)`. | **0** controls under 44px with a coarse pointer, at any width — so an iPad at 1194px now gets stylus sizing while keeping the desktop layout. Fine pointer keeps compact density with **0** under the 24×24 WCAG floor. |
| 4 | **Contrast.** Dual-tone focus ring; `--line-control` for control boundaries. | Ring passes ≥3:1 on all 8 surfaces including sketch paper (was 1.08:1) and the accent/authority fills. `--line-control` clears 3:1 on all 6 surfaces. `.sketch-stage-meta` 2.55 → **5.06:1**. Added `forced-colors` support. |
| 5 | **Dialogs.** Native `<dialog>` + `showModal()`, focus restored, combobox ARIA. | Verified: focus contained through 14 Tab presses; Escape closes; focus returns to the invoking control; `aria-activedescendant` resolves to a real option. Dialogs open on their title, not the close button. |
| 6 | **Affordances.** All six labelled, fixed, or disabled. | Version rail disables archived stages with a reason; `Fit · 64%` → `Fit`; markup shows a "Scratch markup — not saved" notice with a Clear action; "Needs work" → "Regenerate" (matching what it does); audio lanes marked illustrative. **Also fixed a real bug:** markup bled across shots when switching via the command palette. |
| 7 | **Sketch persistence.** `sessionStorage` → `localStorage`, with migration. | A stroke now survives a full page reload; previously destroyed. "Saved locally HH:MM" indicator, and a distinct failure state for quota/private-mode. |
| 8 | **State model.** Failed/Cancelled jobs, skeleton, empty states. | Failed jobs surface the provider error with Refresh and Dismiss (previously invisible — the dock only rendered Queued/Running). Board skeleton replaces the full-screen spinner. Empty states for zero shots and zero authorities. **Also fixed:** an empty project used to sit on the loading screen forever. |

Two further fixes surfaced during the work:

- **Drawer animation.** `drawer-in` ramped opacity from `.4`, dropping drawer text to ~2.9:1 while animating. It now animates transform only.
- **`<dialog>` Escape fallback.** Not every engine emits `cancel` for a modal dialog, and this project ships iPad WebKit tests, so `Dialog` also listens for Escape directly.

### Verification

| Gate | Result |
|---|---|
| `npx tsc --noEmit` | passes |
| `npm run build` | passes |
| Playwright (desktop + iPad WebKit) | **10/10 passing** |
| `axe-core` serious/critical | 0 violations |
| .NET test suite | **6/6 passing** |
| `npm audit --audit-level=high` | 0 vulnerabilities |
| `git diff --check` | clean |

Two tests were updated, both because the behaviour they asserted changed correctly:
the command palette input is now exposed as a `combobox` rather than a `textbox`,
and the comparison-mode selector is now a pressed-button group rather than a
misapplied tab set. No test was
weakened to make it pass.

### Post-review audit

The incoming pass was re-read against the implementation before commit. Four
residual gaps in the review claims were corrected: focusing a shot card no
longer changes selection, the sketch state no longer shadows the browser
`document`, tab sets now expose their panels and keyboard movement, and the
comparison wipe supports Home, End, Page Up/Down, both arrow axes, and an
announced value. The failed-job action was also relabelled from `Retry` to
`Refresh` because it does not yet resubmit a job.

### Not done

- **Light theme.** Still dark-only. This is a deliberate product decision to record,
  not an oversight — but it should be recorded.
- **Server persistence follow-through is now implemented** for sketch strokes,
  labels, and creative briefs. Imported underlays and scratch markup still need
  the Phase 1 asset store.
- **Shot and authority creation.** Still absent from the UI. The empty states now name
  it as Phase 1 work rather than leaving a blank grid.

---

## Sources

- [WCAG 2.2 SC 2.5.8 Target Size (Minimum)](https://www.thewcag.com/criteria/2.5.8) — 24×24 CSS px is a floor, not a target
- [Target Size — accessibility guide](https://www.webability.io/glossary/target-size) — 44×44 as the common best-practice benchmark; Apple 44×44pt, Material 48×48dp
- [WCAG minimum font size — The A11Y Collective](https://www.a11y-collective.com/blog/wcag-minimum-font-size/) — no hard WCAG minimum, but 16px baseline for body; relative units required for user scaling
- [Minimum font size — Accessible Web](https://accessibleweb.com/question-answer/minimum-font-size/)
- [Font size requirements guide](https://font-converters.com/accessibility/font-size-requirements) — 12px is below recommended baselines for primary content
- [How to Build Accessible Modals with Focus Traps (2026) — UXPin](https://www.uxpin.com/studio/blog/how-to-build-accessible-modals-with-focus-traps/) — focus containment and restoration requirements
- [There is No Need to Trap Focus on a Dialog Element — CSS-Tricks](https://css-tricks.com/there-is-no-need-to-trap-focus-on-a-dialog-element/) — native `<dialog>` removes the need for manual focus-trap code
- [Modals, Dialogs, and Accessibility — DubBot](https://dubbot.com/dubblog/2026/modals-dialogs-and-accessibility.html)
- [Dark Mode: How Users Think About It — NN/g](https://www.nngroup.com/articles/dark-mode/) — light mode outperforms for most normally-sighted users; offer dark mode as a choice
- [Keyboard Navigation Patterns for Complex Widgets (2026) — UXPin](https://www.uxpin.com/studio/blog/keyboard-navigation-patterns-complex-widgets/) — toolbar, tablist, and combobox patterns
