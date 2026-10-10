# Laws & Effects

Descriptive principles: they explain *why users behave as they do*. Use them to predict the consequence of a design choice before you commit to it. Each entry gives the idea in original wording, the design consequence, and the trade-off to watch.

## Contents
- Fitts's Law
- Hick's Law
- Miller's Law
- Jakob's Law
- Tesler's Law (Conservation of Complexity)
- Aesthetic-Usability Effect
- Doherty Threshold
- Goal-Gradient Effect
- Peak-End Rule
- Serial Position Effect
- Von Restorff Effect (Isolation Effect)
- Zeigarnik Effect

---

## Fitts's Law
The time to move to and acquire a target grows with distance and shrinks with target size. Big, close targets are fast; small, far ones are slow.

**Consequence:** Make frequent or important targets larger and place them near where the pointer or finger already is. Primary actions deserve size and proximity; destructive actions can be deliberately smaller and farther to slow people down.

**Trade-off:** Enlarging everything destroys hierarchy. Size is a finite signal — spend it on what matters most. Note that the practical minimum size depends entirely on the input device; see the medium skills for the actual numbers (a cursor is effectively one pixel; a fingertip is not).

## Hick's Law
The time to make a decision grows with the number and complexity of the choices.

**Consequence:** Reduce the number of options presented at one moment. Break long lists into categories, hide advanced options behind disclosure, and lead users down a guided path rather than presenting everything at once.

**Trade-off:** Hiding options reduces immediate cognitive load but can cost discoverability and power-user speed. The right cut depends on how often each option is actually used.

## Miller's Law
People can hold only a handful of items in working memory at once (the popular "7±2" is better treated as roughly four chunks for reliable design).

**Consequence:** Chunk information. Group related fields, segment phone numbers and IDs, and don't ask users to remember something from a previous screen to use on this one. Carry context forward instead of taxing memory.

**Trade-off:** Over-chunking can fragment a task into too many small steps. Group by meaning, not just to hit a number.

## Jakob's Law
Users spend most of their time in *other* products, so they expect yours to work like the ones they already know.

**Consequence:** Honor established conventions for common patterns (where the nav lives, what a trash icon does, how search behaves). Familiarity is free usability. Innovate where it adds real value, not on the plumbing.

**Trade-off:** Convention can entrench mediocre patterns. Deviate only when the gain clearly outweighs the relearning cost — and note that "convention" here means broad cross-product familiarity, not slavish per-OS idiom-matching.

## Tesler's Law (Conservation of Complexity)
Every system has an irreducible amount of complexity. The only question is who absorbs it — the user or the design.

**Consequence:** Move complexity into the system wherever possible: sensible defaults, inference, automation. Don't push work onto the user that the application could do itself.

**Trade-off:** Absorbing complexity can remove control or transparency. Some complexity belongs with the user because they need to own the decision; hide the rest.

## Aesthetic-Usability Effect
People perceive attractive interfaces as easier to use, and are more tolerant of minor problems in them.

**Consequence:** Visual polish is not decoration; it buys goodwill and forgiveness. But it can also mask real usability problems in testing.

**Trade-off:** Don't let attractiveness substitute for actual usability. Pretty can hide broken — test for real task success, not just impressions.

## Doherty Threshold
Productivity and engagement stay high when system response to user action is faster than roughly 400ms; beyond that, attention and flow degrade.

**Consequence:** Keep interactions feeling instant. When real work takes longer, show immediate feedback (optimistic UI, skeletons, progress) so the *perceived* response stays under the threshold.

**Trade-off:** Faking speed with animation can backfire if the underlying delay is large and the user is left waiting after the animation ends. Match the feedback to the real duration.

## Goal-Gradient Effect
Motivation increases as people get closer to a goal; visible progress accelerates completion.

**Consequence:** Show progress (steps completed, "2 of 3"), and consider giving a head start (a partially complete progress bar). People finish what looks nearly finished.

**Trade-off:** Fake or padded progress erodes trust once noticed. Keep the gradient honest.

## Peak-End Rule
People judge an experience largely by its most intense moment and its ending, not the average.

**Consequence:** Invest in the emotional peaks (the moment of success, the "it worked") and the endings (confirmation, completion, sign-off). A graceful finish outweighs a dozen forgettable middle steps.

**Trade-off:** Don't neglect the middle to the point of frustration — a bad enough trough becomes the peak that's remembered.

## Serial Position Effect
People remember the first and last items in a series best; the middle blurs.

**Consequence:** Put the most important navigation items and actions at the start or end of a list or bar, not buried in the middle.

**Trade-off:** Only a few positions are privileged; don't try to make everything "first or last."

## Von Restorff Effect (Isolation Effect)
The item that differs from its neighbors is the one that gets noticed and remembered.

**Consequence:** Make the single most important action visually distinct (one primary button, not five). Distinctiveness is how you direct attention.

**Trade-off:** If everything is emphasized, nothing is. Each added highlight dilutes the others — emphasis is zero-sum.

## Zeigarnik Effect
People remember incomplete tasks better than completed ones, and feel tension to finish them.

**Consequence:** Use visible "unfinished" cues (checklists, incomplete profiles, "1 step left") to draw users back to completion.

**Trade-off:** Manufactured incompleteness feels manipulative. Use it for genuinely valuable tasks the user wants to finish, not to trap attention.

---

## Further reading (do not copy verbatim)
Jon Yablonski's *Laws of UX* (lawsofux.com, and the O'Reilly book) is the best-known collection of these and is licensed CC BY-NC-ND — cite it, link it, but write your own descriptions as above rather than reproducing its text.
