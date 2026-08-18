# Changelog

Every stateful feature ships with a changelog entry — MICROMOUND.md design rule 9. This file
exists to answer one question quickly: *what changed about what this thing is allowed to do, and
when?* Entries are newest first. Versions are the `MicromoundVersion` in `Directory.Build.props`,
which always matches the release tag.

Because this repository governs physical actuation, entries call out separately anything that
**narrows or widens authority**, changes the **canonical wire bytes**, or alters a **refusal
reason**. A device in the field is only as safe as the oldest firmware still talking to it, so a
wire change is never a footnote here.

---

## v0.2.3 — one owner for the release

Tooling and docs only. No `src/`, no tests, no wire change.

Groundwork for the CI and release workflows landing next, done first and separately so that when
those workflows arrive they are the only change on their own push — the one thing `dotnet test`
structurally cannot validate should never share a push with something it could.

### Changed

- **`scripts/release.*` no longer creates the GitHub Release when a release workflow exists.**
  `.github/workflows/release.yml` re-verifies the tag against `MicromoundVersion`, rebuilds and
  retests *from the tag*, publishes `linux-x64` / `linux-arm64` / `win-x64` binaries, and opens a
  **draft** for a human to read and publish. These scripts cannot attach binaries, so they must
  not create the release first: two owners is a race, and the loser is whichever one was carrying
  the artifacts. The scripts now tag, push, and point at the run.

  The check is `if the workflow file exists`, which is deliberate rather than a flag. It is what
  makes this release possible at all — the workflow does not exist yet, so `v0.2.3` is still cut
  by the script, and the handover happens by itself the moment the workflow lands. Nothing is
  released twice and nothing goes unreleased in between.

- **Draft, not published.** Worth stating as a choice rather than a default inherited from
  ANTHILL: a release is the project speaking in its own voice, and unattended automation should
  not get to do that. The workflow assembles everything and stops.

### Added

- **`**Current version:**` marker in `README.md`,** and `scripts/validate.*` now fails when it
  disagrees with `Directory.Build.props`. The incoming CI compares all three of props, README and
  CHANGELOG; checking it locally means a forgotten bump fails on the machine that can fix it in a
  second, rather than on a runner ten minutes later. Same reason the changelog check already lives
  there.

### Note on what is coming

`origin/ci-train` carries the workflows this release prepares for, but it is rooted on `v0.1.0`
and against current main it is `+1256 / −7267` — merging it would delete `Micromound.Capabilities`,
`Micromound.Runtime`, `Micromound.Sync`, every kernel test, and both v0.2.1 validation suites. The
`.github/**` files get lifted out of it onto current main; the branch itself is not merged. Four of
its fourteen safety guards already point at things that moved in v0.2.0 and will be repointed on
the way in.

## v0.2.2 — the release script releases

Tooling only. No `src/`, no tests, no wire change.

`v0.2.1` was tagged by hand because `scripts/release.ps1` crashed before it reached its own
confirmation prompt. Both halves of the failure were in the PowerShell layer rather than in the
release logic, and both are the kind that only appear on a real run:

### Fixed

- **`$ErrorActionPreference = "Stop"` plus a native command is a trap.** Windows PowerShell turns
  anything a native command writes to stderr into an `ErrorRecord`, and under `Stop` that
  terminates the script — `2>$null` redirects the stream, not the record. `git rev-parse v0.2.1`
  on a tag that does not exist yet writes to stderr *as its way of saying "no such tag"*, so the
  script died on the good news. Both `.ps1` files now run under `Continue` and check
  `$LASTEXITCODE` explicitly, with `-ErrorAction Stop` on the cmdlets that genuinely must not
  fail. The same trap was live in `validate.ps1`: one NuGet warning on stderr from a *passing*
  build would have aborted validation and read exactly like a test failure.
- **Tag existence is now read from output, not from an exit code.** `git tag --list` and
  `git ls-remote --tags` print the match or nothing and always succeed, so there is no stderr for
  PowerShell to trip over.
- **An unreachable remote no longer reads as "the tag is free".** `git ls-remote` prints nothing
  both when a tag does not exist and when it cannot reach the remote at all, and only the exit
  status tells the two apart. The first version of this check looked at output alone, so a network
  failure would have been reported as a passing gate — a failure to check presented as a pass,
  which is the same shape as `ModuleBoundaryTests` silently not looking at Micromound. Found by
  running the new dry run against a remote that did not exist.
- **Repository files are read as UTF-8.** PS 5.1 reads a BOM-less UTF-8 file as ANSI, which turns
  every em dash in `CHANGELOG.md` into mojibake — and those bytes become the published release
  notes. Caught before it shipped, but only because the first bug stopped the run.

- **Two xUnit2013 warnings introduced in v0.2.1 are gone.** `Assert.Equal(1, errors.Count)` is
  now `Assert.Single(errors)`. The long form was chosen to dodge an overload-ambiguity risk that
  does not exist — `Assert.Single` on an `IReadOnlyList<string>` binds the generic overload
  cleanly, as the rest of this suite already demonstrated. Caution about a hazard that was never
  there still cost two warnings and a less readable assertion.

### Added

- **`--dry-run` / `-DryRun`** on both release scripts. It evaluates every gate, prints a pass/fail
  table, and tags nothing. This exists because of exactly what happened here: the only way to
  discover that the release script could not release was to attempt a release. A gate that can
  only be exercised by the irreversible operation it guards is not a gate anyone can trust.
  Outside a dry run the first failure is still fatal — a release must not walk past a red gate
  because the ones after it happen to be green.

### Not fixed, and worth naming

Neither script has an automated test. `--dry-run` makes them *exercisable* on demand, which is a
real improvement over "find out at the tag", but it is not the same as something that runs without
being asked. There is also still no CI in this repository at all: every green this project has ever
had came from one Windows machine. Both belong to the same piece of work.

## v0.2.1 — M0 frozen

Milestone M0 is complete: the wire contracts, the identity layer, and the capability kernel are
now all covered by tests, and this file exists. No production code changed in this release — it
closes the three items `ROADMAP.md` listed as remaining, one of which turned out to be a
bookkeeping error rather than work.

### Added

- **`MissionValidationTests`** — 23 test cases over `MissionValidator`, which shipped in v0.2.0 with no
  direct coverage. The suite validates the worked example from `ARCHITECTURE.md` "Structured work"
  as its passing case, on the principle that a design doc whose own example does not validate is a
  doc that is wrong. It pins: charter identity, mound identity, expiry, per-op capability and
  routine authorization, the mission's own `allowed_routines` narrowing the charter, the closed
  condition-operator set, backward-only condition references, and evidence promised by the mission
  that no step is tagged to produce.
- **`ManifestValidationTests`** — 29 test cases over `ManifestValidator`, likewise uncovered. Pins:
  mound identity, safe state, the reasoning-mode/provider pairing, driver availability against a
  build's actual driver set, capability-id well-formedness, the `routine.` namespace, worker
  uniqueness and ceilings, offline behaviours, and device limits keyed to nothing the mound
  declares.
- **`CHANGELOG.md`** — this file, backfilled to the first tag.
- **`scripts/validate.sh` + `.ps1`, `scripts/release.sh` + `.ps1`** — one validation
  command and one guarded release command, mirroring ANTHILL's. Validation refuses to run
  while `MICROMOUND_UPDATE_GOLDEN` is set, because a golden test that rewrites its own
  expectation reports the same green as one that verified something; and it fails when
  `Directory.Build.props` and `CHANGELOG.md` disagree about the version, which is design
  rule 9 checked before the PR rather than at the tag. `release.sh` refuses to tag unless
  the tree is clean, main is synced, the section exists, and the tag is free — then builds
  the GitHub Release notes from that same CHANGELOG section, so there is one source for
  them rather than two. Both scripts exist in bash and PowerShell because this project is
  developed on Windows without bash on PATH and released from the same machine — a release
  step that only runs under a shell the maintainer does not have is not a release step.

### Fixed

- `ROADMAP.md` listed "regenerate the golden fixtures for the amended v0 contracts" as remaining
  M0 work. That was done in `901f4dc` and merged as part of v0.2.0; only the checkbox was left
  unticked. The list now reflects what the repository actually contains.

### Authority, wire, refusals

Unchanged. No new grant, no new refusal reason, no change to canonical bytes. The golden fixtures
are byte-identical to v0.2.0.

### Known gaps, recorded rather than quietly fixed

Writing the two suites surfaced three things the v0 validators do not check. None is a regression
and none is fixed here, because each is a contract decision rather than a bug, and a release that
adds tests should not also change behaviour:

- `MissionValidator` does not require a `sense` step's capability to be in the `sense.` namespace,
  nor an `act` step's to be in `act.`. A mission can therefore `sense` an actuator id. The kernel
  still refuses at execution on class and grant, so this is a validation gap, not an authority
  one.
- `Mission.safe_state` and `Mission.worker` are accepted unvalidated. Nothing yet requires a
  mission's safe state to be compatible with the manifest's.
- `ManifestValidator` does not check `WorkerDefinition.exposes`, `runtime_type`, or
  `required_evidence`.

These belong to M1, where the runtime that consumes them lands.

---

## v0.2.0 — the capability kernel, and a standalone mission

The restructure. MicroMound stopped being described as ANTHILL's device arm and became a runtime
with an abstract **upstream controller**; ANTHILL is named as the reference implementation in
`UPSTREAM.md` and appears nowhere in the contracts.

### Added

- **`Micromound.Capabilities` — the capability kernel.** The single physical authority boundary.
  Thirteen ordered checks (stop, registration, driver health, hazardous, class and lease, grant,
  worker ceiling, parameters, limit intersection, duty cycle, rate, clamp, executor bound), each
  with a structured refusal reason. `Authorize` is pure and `Execute` is the only path that moves
  anything; drivers are reachable only through `ICapabilityExecutor`, and executors are held only
  by the kernel.
- **Three-tier limit intersection** — hardware ∩ device manifest ∩ charter, ceilings taking the
  minimum and floors the maximum (`Limits.cs`). `AttemptsToWiden` exists so a widening attempt can
  be *reported* even though intersection makes it harmless.
- **Structured missions and manifests** — `Mission.cs` (ordered steps, one-source/one-operator
  conditions, an advisory-only `context` field no runtime path may branch on) and `Manifest.cs`
  (hardware bindings, declared workers, device limits, reasoning configuration).
- **`Micromound.Runtime`** (Mound Major and the six default ants), **`Micromound.Drivers`**,
  **`Micromound.Evidence`**, **`Micromound.Sync`**, **`Micromound.Reasoning`**, and
  **`Micromound.Host`** as project scaffolding with documented boundaries.
- **`CapabilityId`** — the closed `sense.` / `act.` / `routine.` namespace with strict
  well-formedness, and `CapabilityPattern.MatchesAny`.
- Docs: `ARCHITECTURE.md`, `ANTS.md`, `CAPABILITIES.md`, `CONFIGURATION.md`, `UPSTREAM.md`,
  `ROADMAP.md`. `MICROMOUND.md`, `PROTOCOL.md` and `SAFETY.md` rewritten.

### Changed

- **"Edge Queen" is now "Mound Major".** `src/Micromound.EdgeQueen/` removed.
- **Layer 1 enforcement moved out of the simulator and into the kernel.** In v0.1.0 the clamping
  lived on `SimMound`'s own actuation path, which meant the simulator and any future runtime could
  have diverged. `Micromound.Sim` was rebuilt on top of the real kernel, so a passing simulator
  test is now a statement about the code a Pi will run.
- **Milestones renumbered.** ESP32 firmware M3 → **M5**; optional reasoning is now **M6**, last on
  purpose. M0 was re-scoped from "protocol only" to "contracts *and* kernel".
- **Reasoning is structurally subordinate.** `Micromound.Reasoning` does not reference
  `Micromound.Capabilities`, so a provider cannot call the kernel, hold an executor, or touch a
  driver. Adding one would be a visible `.csproj` change rather than a line inside a method.

### Wire — **breaking within v0**

Protocol version stays `0`; the golden fixtures were regenerated in `901f4dc`. Any implementation
built against v0.1.0 bytes must be rebuilt.

- New envelope kinds: `config` (downlink declarative configuration) and `mission_report` (uplink
  structured outcome). Neither is in the reduced profile — a controller's hardware map is compiled
  in, and it runs charter-selected routines rather than open work packets.
- `Charter` gained `routines`. A charter selects from behaviour that already exists; it can enable
  a registered routine and narrow its parameters, and can never define one.
- `ActionRecord` gained `mission_id`, `routine_id`, `requested_parameters`, and
  `evidence_required`. Reporting only the effective parameters would hide a clamp from the audit
  trail that exists to surface it.
- `CapabilityLimits` gained `max_rate_per_h`.
- `sig` is documented as **zeroed, not omitted**, inside canonical bytes. A C mirror written to
  "exclude the signature" would drop the field and produce a different digest for identical data.

### Authority

Narrowed, in two places worth naming:

- Registration-time refusals, so a misconfigured device fails at startup rather than at first use:
  a `sense.` capability may not be classed above `observe`, nothing may be registered as
  `hazardous`, and a routine may not be classed below a capability it drives.
- `hazardous` is refused before authority is consulted at all, so no charter can ever be the
  reason it was allowed.

---

## v0.1.0 — signing, and bytes that stay put

### Added

- **`Micromound.Crypto`** — Ed25519 device identity, signing, and verification (BouncyCastle
  2.5.0, since .NET 9 has no Ed25519 in the BCL). `Micromound.Protocol` stays dependency-free and
  declares the discipline only: `IEnvelopeSigner`, `IEnvelopeVerifier`, `IPublicKeyDirectory`.
- **Signature enforcement.** PROTOCOL.md §2 had always said unsigned or badly signed envelopes are
  dropped and audited; nothing checked `sig` until now. Refusals carry a specific reason —
  `missing`, `malformed_format`, `unsupported_algorithm`, `unknown_key`, `bad_signature` — never a
  bare no.
- **Golden-file wire fixtures** freezing the canonical bytes for the future C mirror.
- **`EvidenceGate`** — "commands are not evidence" as a pure function. `succeeded` and `clamped`
  survive only when the referenced evidence resolves, parses, and is fresh; everything else
  degrades to `unverified`.
- **`ProtocolTime`** — one wire timestamp format, strict on the way out and tolerant on the way
  in.

### Fixed

The golden fixtures caught two encoding bugs on their first run, before any device existed to be
broken by them:

- The default `System.Text.Json` encoder escaped `+` as `+` and `"` as `"`. Fixed with
  `UnsafeRelaxedJsonEscaping`.
- Timestamps serialized as `…0000000+00:00`, disagreeing with PROTOCOL.md §2's
  `2026-08-14T21:04:11Z`. Fixed by `ProtocolTime`, and §2 gained a normative encoding block.

Both would have surfaced in the field as a device whose envelopes could not be verified.

### Wire — **breaking within v0**

- `ActionRecord` gained `detail`. SAFETY.md prohibits silent failure, so every non-success outcome
  carries its reason on the wire.
- Lease expiry now yields `quiesced` rather than dropping the charter; the charter is retained for
  reporting.
- Refused actuations are queued for the controller rather than dropped.

---

## v0.0.1 — M0 foundation

Design docs, the v0 protocol contracts (envelopes, canonical bytes, digests, hash chaining,
charters, leases, action classes, evidence), the in-memory simulator, and the network-free
authority tests.
