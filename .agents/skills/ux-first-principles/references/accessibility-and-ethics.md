# Accessibility & Ethics

Constraints that apply to *every* design decision, not a final audit. Accessibility widens who can use the design; ethics keeps it honest. Treat both as inputs to the decision, the same way you treat hierarchy or cognitive load.

## Contents
- WCAG and the POUR principles
- Practical accessibility defaults
- Dark patterns to avoid
- Inclusive-design stance

---

## WCAG and the POUR principles
The Web Content Accessibility Guidelines (a W3C standard) organize accessibility under four principles. They apply to application UI generally, not only web. Conformance has levels (A, AA, AAA); **AA is the common target.**

- **Perceivable.** Users must be able to perceive the information — it can't be invisible to all their senses. Provide text alternatives for non-text content, captions for media, sufficient color contrast, and don't convey meaning by color or sound alone.
- **Operable.** Users must be able to operate the interface — by keyboard as well as pointer/touch, with enough time, without content that risks seizures, and with clear ways to navigate and know where they are.
- **Understandable.** Information and operation must be understandable — readable text, predictable behavior, and input help that prevents and explains errors.
- **Robust.** Content must work reliably with current and future tools, including assistive technologies — use standard, well-formed, properly-labeled components so screen readers and other AT can interpret them.

## Practical accessibility defaults
Bake these into decisions rather than retrofitting them:

- **Contrast:** meet at least the AA text-contrast ratios; ensure meaningful non-text UI (icons, focus rings, control boundaries) is distinguishable too.
- **Never color alone:** pair any color-coded meaning (error red, success green, status dots) with text, icon, or shape, so it survives color blindness and grayscale.
- **Keyboard path:** every action reachable and operable by keyboard, with a visible focus indicator and a logical focus order. (On desktop this is essential; see the desktop medium skill.)
- **Targets and spacing:** interactive targets large enough and spaced enough to hit reliably — the actual minimums depend on input device; see the medium skills.
- **Labels and names:** every control has an accessible name; icons that act as buttons are labeled; form fields have persistent, programmatically-associated labels (not placeholder-only).
- **Honor user settings:** respect reduced-motion, increased-contrast, larger-text, and reduced-transparency preferences instead of overriding them.
- **Text resizing and reflow:** layouts survive larger text and don't trap content; don't disable zoom.
- **Motion and timing:** avoid flashing content; give users control over auto-playing or time-limited interactions (ties to the Doherty Threshold for feedback, but never at the cost of seizure safety or user control).

## Dark patterns to avoid
Ethics is part of first-principles design: an interface can be usable *and* manipulative. Reject patterns that exploit the user's psychology against their interest, even when they "convert":

- **Forced continuity / hard-to-cancel:** making sign-up trivial and cancellation buried (asymmetry of effort).
- **Confirmshaming:** guilt-tripping the decline option ("No thanks, I hate saving money").
- **Misdirection / false hierarchy:** using Von Restorff emphasis to steer users toward the choice that benefits the business, not them.
- **Sneaking / hidden costs:** revealing fees or added items late in a flow (a betrayal of the Peak-End ending).
- **Fake urgency or scarcity:** manufactured countdowns and "only 1 left" that aren't true (a dishonest Goal-Gradient / Zeigarnik nudge).
- **Nagging and trick questions:** repeated interruption, or wording designed to be misread.

The same psychological effects that make interfaces *good* (emphasis, progress, urgency, defaults) become dark patterns when aimed against the user's interest. The line is whose benefit the effect serves. Keep nudges honest.

## Inclusive-design stance
Designing for the edges improves the center: captions help in noise, large targets help in motion, high contrast helps in sunlight, clear language helps everyone under stress. Treat the "extreme" user (one hand, low vision, low attention, low bandwidth, second language) as a design input, not an afterthought. Accessibility done early is mostly free; bolted on late, it's expensive and worse.

---

## How to apply these in a decision
For every decision, ask: Can someone perceive this without relying on color or sound alone? Can they operate it by keyboard and touch? Will it survive larger text and reduced motion? And — is any persuasive technique here serving the user, or working against them? If a choice fails one of these, fix the choice, don't add a disclaimer.
