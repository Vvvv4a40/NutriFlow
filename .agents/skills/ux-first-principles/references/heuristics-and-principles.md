# Heuristics & Principles

Normative principles: they state *what to aim for*. Where laws & effects predict behavior, these prescribe quality. Use them both as design guidance and as the rubric for a heuristic evaluation (see the design-review subagent).

## Contents
- Nielsen's 10 Usability Heuristics
- Shneiderman's 8 Golden Rules
- Norman's Principles of Interaction

---

## Nielsen's 10 Usability Heuristics
General principles for interaction design, originally developed by Jakob Nielsen. Broad rules of thumb, not specific usability rules — credit Jakob Nielsen / Nielsen Norman Group when reusing.

1. **Visibility of system status.** The interface should always keep users informed about what is going on, through timely, appropriate feedback. People decide what to do next based on what the system tells them it is doing.

2. **Match between system and the real world.** Speak the users' language with familiar words and concepts; follow real-world conventions so information appears in a natural, logical order. Avoid internal jargon.

3. **User control and freedom.** People choose actions by mistake and need a clearly marked way out — undo and redo, cancel, a visible exit — without having to commit or wade through a long process.

4. **Consistency and standards.** Users should not have to wonder whether different words, situations, or actions mean the same thing. Follow platform and industry conventions (Jakob's Law in practice).

5. **Error prevention.** Better than good error messages is a design that prevents the error from happening at all — eliminate error-prone conditions or confirm before committing consequential actions.

6. **Recognition rather than recall.** Minimize memory load by making elements, actions, and options visible. Don't force users to remember information from one part of the interface to another.

7. **Flexibility and efficiency of use.** Let accelerators (shortcuts, defaults, history) speed up expert users without getting in beginners' way. Allow people to tailor frequent actions.

8. **Aesthetic and minimalist design.** Interfaces should not contain information that is irrelevant or rarely needed. Every extra unit of content competes with the relevant units and diminishes their visibility.

9. **Help users recognize, diagnose, and recover from errors.** Error messages should be in plain language, precisely state the problem, and constructively suggest a solution.

10. **Help and documentation.** Ideally the system needs no explanation, but when help is needed it should be easy to search, focused on the user's task, list concrete steps, and not be too large.

## Shneiderman's 8 Golden Rules
Ben Shneiderman's rules of interface design — a complementary, slightly more interaction-oriented set.

1. **Strive for consistency** in actions, terminology, layout, and color.
2. **Enable frequent users to use shortcuts** — let proficiency shorten the path.
3. **Offer informative feedback** for every action, proportionate to its significance.
4. **Design dialogs to yield closure** — group actions into beginning, middle, and a clear end that gives a sense of accomplishment.
5. **Prevent errors**, and offer simple, specific recovery when they occur.
6. **Permit easy reversal of actions** — reversibility relieves anxiety and encourages exploration.
7. **Keep users in control** — make the system respond to user-initiated actions, not the reverse; avoid surprising or changing familiar behavior.
8. **Reduce short-term memory load** — keep displays simple and don't require users to remember information across screens (echoes Miller's Law and recognition-over-recall).

## Norman's Principles of Interaction
From Don Norman's *The Design of Everyday Things* — the vocabulary for whether an interface communicates how it works.

- **Affordances.** The properties of an object that determine how it can be used — a button affords pressing, a slider affords dragging. In UI, the perceived affordance is what matters: does it *look* operable in the way it actually is?
- **Signifiers.** The perceivable cues that tell the user where and how to act — the visual hint that an affordance exists (an underline signifying a link, a handle signifying draggability). Affordances without signifiers are invisible.
- **Mapping.** The relationship between controls and their effects should be natural — arrange and behave controls so that their layout corresponds to what they change (a volume slider that goes up for louder).
- **Feedback.** Every action needs an immediate, informative response so the user knows it registered and what happened (overlaps with Nielsen #1 and Doherty Threshold).
- **Constraints.** Limit the possible actions to guide users toward correct ones and prevent errors — disable what can't be used now, restrict inputs to valid formats.

---

## How to apply these in a decision
Don't run the whole list every time. When making a choice, ask which two or three of these the choice could violate, and design to satisfy them. When *reviewing* an interface, the full lists become a checklist — see the design-review subagent, which scores findings by severity using these as the rubric.
