# PerfectSync

A high-performance replacement for `VRC Object Sync`, written in UdonSharp for VRChat worlds.

Objects sleep by default and cost nothing while asleep. A single manager tracks what is awake,
packs quantized state into batched packets, and a per-player channel puts it on the wire.

**Validated at 600 objects with 4 players**, peaking at ~9.3 KB/s outbound per client.

---

## Requirements

- Unity **2022.3.22f1**
- VRChat Worlds SDK (VCC / VPM)
- UdonSharp (bundled with the Worlds SDK)
- TextMeshPro — for the debug overlay only

---

## Why not just use VRC Object Sync

`VRC Object Sync` is one Continuous Sync behaviour per object. At a few hundred objects that becomes
hundreds of independent packet streams competing for the same ~11 KB/s of Udon bandwidth, and every
object pays a per-frame cost whether or not it is doing anything.

PerfectSync inverts both:

- **Objects with nothing to say are silent and free.** `SmartSyncObject` declares no `Update`,
  `FixedUpdate` or `LateUpdate`. Unity only pays the call cost for messages a script actually
  declares, so sleeping objects cost literally zero frame time. The manager ticks only its awake set.
- **One batched packet instead of hundreds of small ones.** The manager packs many objects into a
  single Manual Sync payload and sends only when something is actually dirty.

---

## Files

| File | Role |
|---|---|
| `SpatialGrid.cs` | Uniform spatial hash. Answers "which ids are near this point" and nothing else. Allocation-free. |
| `SmartSyncObject.cs` | Per-object state machine, sleep/wake hysteresis, pickup handling, held hand pose. |
| `SmartSyncManager.cs` | Registry, awake set, dirty set, interest management, packet codec. The only `Update` in the system. |
| `SmartSyncChannel.cs` | Per-player transport. Owns the synced byte array and calls `RequestSerialization`. |
| `SmartSyncDebugOverlay.cs` | Runtime readout: awake count, bandwidth, grid health, frame cost. |
| `SmartSyncStressTest.cs` | Scatter / impulse / settle operations for load testing. |
| `Editor/PerfectSyncSetup.cs` | Editor menu item that builds the debug canvas. Not an UdonSharpBehaviour. |

---

## Setup

**1. Sync root**

Create an empty GameObject named **`SyncSystem`** and add both `SpatialGrid` and `SmartSyncManager`.
Assign the grid to the manager's `grid` field.

The name matters: the channel, overlay and stress test all resolve the manager by looking for a
GameObject with this name, so they need no inspector wiring. Change it via `managerObjectName` on
each if you prefer a different name.

**2. Channel pool**

Add child GameObjects `Channel_0` … `Channel_N`, each with a `SmartSyncChannel`. They self-register,
so the manager's `channels` array can be left empty.

> **Size the pool for a full instance, not your test.** Each player claims one channel. The **n+1**th
> player in a pool of **n** gets `No free sync channel` in the log and **cannot publish anything they
> own** — they see the world fine, but nobody sees their objects move. Unclaimed channels are free,
> so 32 is a reasonable default.

**3. Synced objects**

Every networked object needs `SmartSyncObject`, a `Rigidbody`, and optionally `VRC Pickup`.

Set `manager` on the **prefab** so every instance self-registers. Registration is ordering-safe — the
manager initializes on first call regardless of which `Start()` runs first.

`SmartSyncObject` is deliberately the one component that does *not* auto-resolve the manager by name:
it is the one you duplicate hundreds of times, and hundreds of `GameObject.Find` calls at load would
scan the whole scene each time.

**4. Debug overlay** *(optional)*

**Tools → PerfectSync → Create Debug Canvas** builds a world-space canvas with the overlay wired up.

If the text renders blank or magenta, import TMP essentials:
**Window → TextMeshPro → Import TMP Essential Resources**.

**5. Set `worldExtent`**

This is the one value you **must** change. Positions are quantized to 16 bits per axis across
`worldExtent × 2`, and anything outside that volume **clamps to the boundary**. Set it to cover your
world and no more — smaller means finer precision. At `100` you get roughly 3 mm.

---

## Settings

Measured against 600 objects with 4 players.

| Component | Setting | Value | Why |
|---|---|---|---|
| SpatialGrid | `cellSize` | `10` | half of `interestRadius` — see Tuning |
| | `maxObjects` | `1024` | must exceed object count; match the manager |
| | `use2D` | floor-based worlds only | turns the query sweep from cubic to quadratic |
| Manager | `interestRadius` | `20` | the main lever at high object counts |
| | `interestInterval` | `0.5` | relevance changes far slower than physics |
| | `sendInterval` | `0.15` | fewer serialization calls than `0.1` |
| | `worldExtent` | cover your world | sets position precision; clamps outside |
| Channel | `maxPacketBytes` | `1400` | `1400 ÷ 0.15` = 9.3 KB/s, under budget |
| Object | `sleepTickCount` | `20` | sleep sooner; awake count is everything |
| | `positionDeltaThreshold` | `0.015` | fewer dirty marks, invisible at distance |
| | `rotationDeltaThreshold` | `1.0` | same, in degrees |
| | `neighbourWakeRadius` | `1.5` | raise it if your objects are large |

### Bandwidth ceiling

```
maxPacketBytes ÷ sendInterval  ≤  ~10,000 B/s
```

Udon's practical outbound budget is roughly 11 KB/s per client. Fewer, larger packets are gentler on
the call-rate limiter than many small ones.

---

## Tuning the grid

The instinct is to shrink `cellSize` until bucket chains are short. **That is backwards.**
`QueryRadius` sweeps a cell range derived from the interest radius, so finer cells mean *more* cells:

```
span        = ceil(interestRadius / cellSize)
cells swept = (2 * span + 1)^3
```

At `interestRadius = 24`:

| `cellSize` | span | cells swept | |
|---|---|---|---|
| 3 | 8 | 4,913 | absurd — thousands of cells to find a few hundred objects |
| 8 | 3 | 343 | |
| 12 | 2 | 125 | the knee of the curve |
| 24 | 1 | 27 | minimal cells, heavy overdraw |

Two costs pull opposite ways: cells swept shrinks as cells grow, while objects examined grows, since
a coarse grid sweeps a cube far larger than the sphere. The balance point is:

> **`cellSize` ≈ `interestRadius` ÷ 2**

If you need fine cells for a dense pile but fine cells explode the sweep, lower `interestRadius`,
not `cellSize`. Halving the radius lets you halve the cell size at identical sweep cost, and it also
cuts how many objects stay relevant.

---

## Wire format

A 4-byte header (version, sequence, record count) followed by variable-length records.

| State | Payload | Record size |
|---|---|---|
| Sleeping / Teleport | pos 6 + rot 4 | 13 B |
| Physics | pos 6 + rot 4 + vel 6 *(optional)* | 13–19 B |
| Held | player 2 + hand-local pos 6 + rot 4 | 15 B |

- **Position** — 16 bits per axis across `worldExtent × 2`
- **Rotation** — smallest-three in exactly 4 bytes: 2 bits naming the largest component, 10 bits each
  for the other three. The largest is recovered from unit length, and since `q` and `-q` are the same
  rotation, its sign never has to be sent.
- **Velocity** — 16 bits per axis across ±`maxSpeed`, omitted entirely below `velocityCutoff`
- **Held pose** — relative to the hand bone, reconstructed on remotes every frame. Locked to the
  hand, no interpolation, far cheaper than world space.

Two ownership rules are enforced in the codec: a client only ever **writes** objects it owns, and
**discards** received records for objects it owns. Applying a stale remote copy of something you own
is what makes a held object snap backwards in your hand.

---

## Reading the overlay

| Reading | Healthy | What bad means |
|---|---|---|
| `awake / total` | → 0 when settled | Stuck high: objects are not reaching the sleep thresholds. |
| `dirty` | near 0 | Tracking `awake`: non-owned objects are entering the queue and clogging it. |
| `rate` | 0 B/s at rest | Never zero: something is marking dirty every tick. |
| `failed` | flat | Climbing in steady state: `sendInterval` too aggressive for the packet size. |
| `longest` | single digits | Far above the bucket average: objects clustered into few cells. |
| `query overflow` | absent | 512+ objects inside one interest radius — shrink the radius. |

A burst of `failed` when a player joins is expected, not a fault: the late-join snapshot marks every
locally-owned object dirty at once. Refused packets are re-queued rather than dropped, so nothing is
lost — only delayed.

---

## Wake sources

A sleeping object is invisible to the system — not ticked, not sent. Every way one can start moving
again must therefore be an explicit event. This list is exhaustive.

| Source | Notes |
|---|---|
| `OnPickup` / `OnDrop` | Holder's client only. `OnDrop` extends the awake window to a full second so a throw arc actually transmits. |
| `OnOwnershipTransferred` | Fires on every client. New owner forces a full snapshot; everyone else drops queued state. |
| `OnCollisionEnter` | Owner only — remotes are replaying a pose, not simulating. |
| `OnCollisionExit` | Losing contact means whatever held this up just left. Also calls `body.WakeUp()`. |
| `manager._WakeNear()` | Fires on the transition into Held, on every client. |
| `_ForceWake()` | Manual entry point for any other system. |

### The stack case

Four cubes stacked, someone grabs the bottom one. On the owner's client the three above are asleep in
both systems, and the grabbed cube is now remotely owned — so it is kinematic and driven by network
updates. **Unity does not wake a sleeping rigidbody when a kinematic support moves out from under
it.** `OnCollisionEnter` cannot help either: it only fires on *gaining* contact, which here happens
after the fall.

`OnCollisionExit` catches it directly. `_WakeNear` covers the ownership problem — the objects above
are usually owned by a *different* player, and only that player's client can wake and publish them,
so the wake has to fire on every client that sees the grab arrive.

---

## Known limitations

- **`STATE_ATTACHED` is defined but never produced.** The constant and read path exist; nothing sets
  it. Parenting objects to each other is not implemented.
- **No per-receiver filtering.** Interest management controls *waking*, not who receives what — every
  packet goes to everyone. Fine at moderate player counts.
- **Slot ids are never recycled.** `_Unregister` deliberately leaves the slot in place, because ids
  are baked into in-flight packets. Runtime object pooling needs a generation counter first.
- **Remote timing uses local `Time.time`**, not `Networking.SimulationTime`. Works, but proper
  interpolation would need the switch.
- **No delta compression.** Records carry absolute positions.

---

## Stress testing

`SmartSyncStressTest` provides `_Scatter()`, `_Impulse()`, `_Settle()` and `_ToggleChurn()`. Hook them
to UI buttons; `Interact()` fires `_Impulse()`.

It does **not** spawn objects — Udon's only instantiation path is network instantiate, which is rate
limited and nothing like real steady-state load. Place objects at edit time and this drives activity
through them.

Every operation batches through a rolling cursor (`batchSize`, default 25). Moving an object requires
owning it, and VRChat rate-limits ownership transfers hard — grabbing 500 objects at once floods the
network and you end up measuring the flood instead of the sync system.

`_Settle()` is the most valuable one to run: it confirms the awake count returns to zero and
bandwidth goes silent, which is the single most important property of the system.
