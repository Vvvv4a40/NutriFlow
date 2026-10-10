# Compositional & Perceptual

How the human eye organizes a surface before the user has read a word. These govern layout, grouping, emphasis, and "what does the user see first." Most "this feels cluttered / confusing / off" complaints resolve to a violation in this bucket.

## Contents
- Visual hierarchy
- Contrast
- The Gestalt grouping principles (proximity, similarity, common region, continuity, closure, figure/ground)
- Alignment
- Progressive disclosure
- Consistency

---

## Visual hierarchy
The deliberate arrangement of elements so the eye is led through them in order of importance. The single most important compositional principle: it answers "what should the user notice first, second, third?"

**Tools that create it:** size, weight, color/contrast, position (top and left lead in left-to-right cultures), whitespace, and isolation. A clear hierarchy means one dominant element, a small number of secondary ones, and the rest receding.

**Failure mode:** flat hierarchy — everything the same size and weight — forces the user to scan linearly and decide for themselves what matters. Emphasis is zero-sum (Von Restorff): emphasizing everything emphasizes nothing.

## Contrast
Difference along any visual dimension (light/dark, large/small, color, shape) that makes elements distinguishable. Contrast is the raw material of hierarchy and the foundation of legibility.

**Consequence:** Use strong contrast to separate foreground from background and to mark the most important element. Use low contrast to let supporting elements recede.

**Constraint:** Contrast is also an accessibility requirement — text and meaningful UI must meet minimum contrast ratios (see accessibility reference). Never carry meaning by color contrast alone.

## The Gestalt grouping principles
The mind perceives organized wholes, not isolated parts. These describe the automatic rules by which it groups what it sees — use them to make structure visible without borders or labels.

- **Proximity.** Elements placed close together are perceived as related. The most powerful and cheapest grouping tool: spacing communicates relationship before any line or box does. Tighten space within a group, widen it between groups.
- **Similarity.** Elements that share visual characteristics (color, shape, size) are seen as belonging together — and as having the same kind of function. Make things that behave alike look alike, and things that differ look different.
- **Common region.** Elements inside a shared boundary (a card, a panel) are perceived as a group, even if far apart. Use enclosure when proximity alone isn't enough.
- **Continuity.** The eye follows lines and curves and groups elements arranged along a path. Aligned elements read as a related sequence.
- **Closure.** The mind fills in gaps to perceive complete, familiar shapes. Allows minimal, implied forms to still read clearly.
- **Figure/ground.** The eye separates a focal object (figure) from its surroundings (ground). Ensure the intended figure is unambiguous; unstable figure/ground is a common source of "I don't know where to look."

## Alignment
Placing elements along common edges or axes. Alignment creates the invisible grid that makes a layout feel ordered and lets the eye move efficiently.

**Consequence:** Align to shared edges and a consistent grid. Strong alignment reduces visual noise and signals care (feeding the Aesthetic-Usability Effect).

**Failure mode:** ragged, near-but-not-quite alignment reads as sloppy and makes scanning harder even when users can't articulate why.

## Progressive disclosure
Show only what's needed now; reveal complexity on demand. The compositional answer to Hick's Law and Miller's Law.

**Consequence:** Lead with the common case and the essential controls; defer advanced, rare, or detail content behind a click, an expander, a second step, or a details pane. The interface stays simple for the many without abandoning the few.

**Trade-off:** Over-hiding harms discoverability and power-user speed. Disclose by frequency of use: common and important stays visible; rare and advanced gets deferred. Never hide essential information behind hover on touch (see medium skills).

## Consistency
The same things look and behave the same way throughout the product (internal consistency), and conform to wider conventions (external consistency — Jakob's Law). Appears here and in the heuristics bucket because it is both a perceptual aid and a usability rule.

**Consequence:** Reuse patterns, positions, and meanings. Consistency lets users transfer what they learn from one screen to the next, lowering cognitive load everywhere.

**Trade-off:** Foolish consistency forces inappropriate patterns onto situations that genuinely differ. Be consistent where things are the same; differ clearly where they're actually different.

---

## How to apply these in a decision
For any layout or grouping decision, ask in order: What is the hierarchy — what comes first? How are related things grouped (proximity, common region) and distinct things separated? Is everything aligned to a shared structure? Is anything shown now that could be disclosed later? Then check that emphasis is spent on the one or two things that matter, not scattered.
