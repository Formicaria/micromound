# Micromound.Reasoning

Optional reasoning. A standard mound does not use this project at all.

Modes are `none` (default), `remote`, and `local`. The default runtime executes structured
workflows, deterministic rules, and pre-defined routines; most physical work never needs a model
and should not pay for one in memory, latency, or failure modes.

## The enforcement is the project file

This project does **not** reference `Micromound.Capabilities`. That is not a convention or a
comment — it is the mechanism. A reasoning provider cannot call the capability kernel, hold an
executor, or touch a driver, because the project reference that would let it does not exist.
Adding one would be a visible change to a `.csproj`, not a line buried inside a method.

So a model on this mound cannot extend a lease, raise an action class, disable a stop, widen a
limit, or self-authorize anything. It can answer a question, and what comes back is called a
`ReasoningProposal` because that is precisely what it is.

`ProposalGuard` is the other half: a proposal that invents an option the caller never offered has
produced a string, not a decision, and is discarded.

Status: M6 — last, deliberately. Deterministic execution has to be mature before intelligence is
added on top of it.
