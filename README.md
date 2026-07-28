# BedWars — Full Isolation of Multiple Matches at the Same World Coordinates

**Plugin:** `BedWars.cs` (Carbon, Rust)
**Idea:** run any number of concurrent BedWars-style matches ("rooms") that physically occupy **the exact same map coordinates**, so that players in one room cannot see, physically collide with, build over, or otherwise interact with the contents of another room — while everything inside their own room still works normally (building, breaking your own/enemy walls, taking damage, etc.).

This is **not** "spread the arenas across different coordinates" (that approach is trivial and almost always the right call). This is the solution for the case where the arenas are *required* to occupy the same spot — which forced a deep dive into the engine's internals.

---

## Why this is needed at all

The standard way to run several copies of a minigame on a Rust server is to place the arenas at different points on the map (or at different heights). That's simple, reliable, and needs none of what follows.

This plugin solves a **different** problem: several matches must exist **in the same place at the same time**. That is technically much harder, because:

- Rust has no built-in support for "multiple copies of the world."
- Network visibility, physical collision, anti-cheat, and the building system are **four independent engine subsystems**. Solving one does not solve the other three.

---

## Architecture: 4 independent isolation layers

| # | What it isolates | Mechanism | Native hook / patch |
|---|---|---|---|
| 1 | Visibility (visual, network) | `CanNetworkTo` hook | Native Carbon hook |
| 2 | Damage & loot | `OnEntityTakeDamage` / `CanLootEntity` | Native Oxide hooks |
| 3 | Chat | `OnPlayerChat` | Native Oxide hook |
| 4 | Physical impassability | Harmony patch on `AntiHack.IsColliderBlocking` | Requires decompilation |
| 5 | Building on occupied coordinates | Harmony patches on `DeployVolume.CheckFlags` + `Vis.Entities<BuildingBlock>` | Requires decompilation |

Each layer solves its own narrow problem. Without any single one of them, the whole system "leaks" — for example, without layer 4, structures are hidden from foreign-room players but they still physically stumble into them.

### Shared registry — `BedWarsRoomRegistry`

All five mechanisms read from the same single source of truth:

```csharp
public static class BedWarsRoomRegistry
{
    public static readonly ConcurrentDictionary<ulong, int> PlayerInstance; // userID -> instanceId
    public static readonly ConcurrentDictionary<ulong, int> EntityInstance; // net.ID -> instanceId
}
```

`instanceId == 0` means "not in the isolation system at all" — a regular player/object outside BedWars, ignored by every check.

---

## Layer 1 — Network visibility (`CanNetworkTo`)

```csharp
object CanNetworkTo(BaseNetworkable entity, BasePlayer target)
{
    int entityInst = entity is BasePlayer ep
        ? GetPlayerInstance(ep.userID)
        : GetEntityInstance(entity.net.ID.Value);

    if (entityInst == 0) return null;             // not our entity — don't touch it
    int targetInst = GetPlayerInstance(target.userID);
    return entityInst == targetInst ? (object)null : false; // false = hide
}
```

`CanNetworkTo` is a native Carbon hook, called by the engine whenever it decides whether to network a given entity to a specific observer. It works for **any** `BaseNetworkable` — players, walls, beds, storage boxes — not just one object type.

**Important gotcha (cost a long debugging session):** changing a value in the `PlayerInstance` dictionary does **not** make the engine automatically re-evaluate visibility for observers who are already "subscribed." If a player already received a structure over the network and then switched rooms, the engine will not ask `CanNetworkTo` again on its own — the structure stays visible until you force it:

```csharp
void ForceNetworkRefresh(BaseEntity entity)
{
    bool wasLimited = entity.limitNetworking;
    entity.limitNetworking = true;   // kicks the entity out of its network group
    entity.UpdateNetworkGroup();
    entity.limitNetworking = wasLimited;
    entity.UpdateNetworkGroup();     // and re-adds it — CanNetworkTo gets re-evaluated
    entity.SendNetworkUpdateImmediate();
}
```

This is called on **all** entities of both the old and new room on every `/bw join`/`/bw leave` (see `RefreshInstanceEntities`).

---

## Layer 2-3 — Damage, loot, chat

Ordinary Oxide hooks, nothing exotic:

- `OnEntityTakeDamage` — compares the attacker's room to the victim's room (the victim can be a structure/bed, not just a player — this was checked separately, since damage against your own room's structures was initially blocked unconditionally by mistake).
- `CanLootEntity` — blocks looting a foreign room's storage container.
- `OnPlayerChat` — intercepts and re-broadcasts the message only to players in the same room, blocking the global chat (`return true`).

---

## Layer 4 — Physical impassability (the hard part)

### Why the obvious approaches don't work

In the order they were tried and ruled out:

1. **`Physics.IgnoreCollision(colliderA, colliderB)`** — does not work with `CharacterController`. This is a documented Unity limitation: `CharacterController.Move()` runs its own internal sweep test that ignores this pairwise setting entirely.
2. **Toggling `collider.enabled`** — the collider is shared server-wide. You cannot make it "solid for one observer and non-solid for another" at the same time — if players from both the owning room and a foreign room are nearby simultaneously, this breaks for one of them.
3. **`Physics.IgnoreLayerCollision(layerA, layerB)`** — this *does* work with `CharacterController` (unlike #1), BUT it's a global, symmetric layer-collision matrix, not a per-player setting. Getting per-player selectivity requires also moving the player themselves onto a dedicated layer — and a typical Rust build only has 2-3 free layers total (see the "History" section below).
4. **`CharacterController.excludeLayers`** — theoretically a per-instance property (inherited from `Collider`), but Rust's `BasePlayer` has **no** `CharacterController` component at all (movement is fully custom) — confirmed empirically via `player.GetComponent<CharacterController>() == null`. Dead end. (The diagnostic commands `bw.layers`/`bw.testobject.setlayer`/`bw.testexclude` in the file are leftovers from this experiment — kept for reference, can be removed.)

### What actually worked: decompilation + Harmony

Decompiling `Assembly-CSharp.dll` (`ilspycmd`) revealed that an object's physical "solidity" for a specific player is actually decided not by client-side collision, but by **server-side anti-cheat validation**:

```
BasePlayer.OnReceiveTick → UpdatePositionFromTick (accepts the position with no geometry check)
     ↓ (separately, batched, across all players)
AntiHack.AreNoClipping → TestAreNoClipping → IsColliderBlocking(collider, ply, ...)
```

`AntiHack.IsColliderBlocking(Collider collider, BasePlayer ply, ...)` is a private static method that **already receives the player as a parameter** (unlike `CharacterController`/layers, where no such per-player hook exists). We patch it:

```csharp
[HarmonyPatch(typeof(AntiHack), "IsColliderBlocking")]
public static class NoClipRoomBypassPatch
{
    static bool Prefix(Collider collider, BasePlayer ply, ref bool __result)
    {
        var entity = collider.GetComponentInParent<BaseEntity>();
        if (entity == null || entity.net == null) return true;

        if (!BedWarsRoomRegistry.EntityInstance.TryGetValue(entity.net.ID.Value, out int entityInst) || entityInst == 0)
            return true; // not one of our entities

        BedWarsRoomRegistry.PlayerInstance.TryGetValue(ply.userID, out int playerInst);
        if (entityInst == playerInst) return true; // same room — normal logic

        __result = false; // foreign room — doesn't block THIS specific player
        return false;
    }
}
```

The key property: the decision is made **fresh on every single call**, for a specific (collider, player) pair — no shared state, no race condition between simultaneous observers from different rooms.

---

## Layer 5 — Building on occupied coordinates

Layer 4 solved "don't get stuck," but not "can't build here — spot's already taken." That turned out to be a **third, separate** system, unrelated to `AntiHack`.

### `DeployVolume.CheckFlags`

```
Construction.UpdatePlacement → DeployVolume.Check(pos, rot, volumes) → CheckFlags(list, volume, ...)
```

`CheckFlags` is the shared choke point for ALL geometry-check variants (`CheckSphere`/`CheckCapsule`/`CheckOBB`/`CheckBounds`), but unlike `IsColliderBlocking` it **does not receive a player at all**. It had to be threaded through via a separate, small patch:

```csharp
[HarmonyPatch(typeof(Construction), "UpdatePlacement")]
public static class BuildPlacementTrackPatch
{
    public static ulong CurrentPlacingPlayerId;
    static void Prefix(ref Construction.Target target) =>
        CurrentPlacingPlayerId = target.player?.userID ?? 0;
}

[HarmonyPatch(typeof(DeployVolume), "CheckFlags")]
public static class BuildOverlapBypassPatch
{
    static void Prefix(List<Collider> list)
    {
        ulong placerId = BuildPlacementTrackPatch.CurrentPlacingPlayerId;
        if (placerId == 0 || !BedWarsRoomRegistry.PlayerInstance.TryGetValue(placerId, out int placerInst) || placerInst == 0)
            return;

        list.RemoveAll(collider => /* foreign-room collider? drop it from the candidate list */);
    }
}
```

`Construction.UpdatePlacement` has `ref Target target` **right in its method signature**, with `target.player` embedded in it — so the Prefix reads the player directly, no reverse-engineered workaround needed. `List<Collider> list` in `CheckFlags` is a reference type, so the Prefix can filter it in place before the original method even starts iterating over it — no need to reimplement the method's logic at all.

### `Vis.Entities<BuildingBlock>` — a second, independent check

Even after fixing `CheckFlags`, building still failed — the "too close" error came from a **third** system, `BuildingProximity.Check`, which scans nearby `BuildingBlock`s directly via `Vis.Entities<T>(pos, radius, list, layerMask)` — completely bypassing `DeployVolume`.

`Vis.Entities<T>` is a **generic method**. It can't be patched via the `[HarmonyPatch]` attribute (a closed generic instance is required for a specific `T`), so the patch is applied manually in code, in `Init()`:

```csharp
var openMethod = typeof(Vis).GetMethods().FirstOrDefault(m =>
    m.Name == "Entities" && m.IsGenericMethodDefinition &&
    m.GetParameters().Length == 5 &&
    m.GetParameters()[0].ParameterType == typeof(Vector3) &&
    m.GetParameters()[1].ParameterType == typeof(float));

var closedMethod = openMethod.MakeGenericMethod(typeof(BuildingBlock));
_harmony.Patch(closedMethod, postfix: new HarmonyMethod(
    typeof(VisEntitiesBuildingFilterPatch).GetMethod(nameof(VisEntitiesBuildingFilterPatch.Postfix))));
```

The Postfix filters the resulting `BuildingBlock` list, removing foreign-room entries — but **only while an active placement is in progress** (`CurrentPlacingPlayerId != 0`), so it doesn't touch every other call to `Vis.Entities<BuildingBlock>` in the game (there are many, and not all of them relate to building).

---

## Where and how the patches are applied

**Important:** the patches are applied **manually from the plugin's own `Init()`**, not via a separate Harmony mod in `HarmonyMods/`.

```csharp
void Init()
{
    _harmony = new Harmony("com.bedwars.noclipbypass");
    _harmony.PatchAll(typeof(NoClipRoomBypassPatch).Assembly); // attribute-based patches
    // + manual patch on the closed generic Vis.Entities<BuildingBlock> (see above)
}

void Unload()
{
    _harmony?.UnpatchAll(_harmony.Id);
}
```

This is a hard-won, deliberate design decision (see the History section below): the patch originally lived as a separate `.dll` in `HarmonyMods/`, which Carbon loads via Doorstop **before** the engine/world even initializes. Simply having one more Harmony mod present at that early stage **crashed the server** during map generation (`TerrainTopologyMap`/`MapImageRenderer`, `Failed to create thread`/`ERROR_NO_SYSTEM_RESOURCES`) — proven empirically: without the mod in `HarmonyMods/`, the server starts up stably.

Moving the patching inside a regular Carbon plugin, invoked from `Init()` (i.e. after the engine has already fully initialized — the same safe window in which other Carbon plugins like `Vanish` routinely patch themselves), eliminated the crash entirely. Bonus: the whole thing is now **hot-reloadable** via `c.reload BedWars`, with zero server restarts.

---

## Commands

### In-game (chat)
| Command | Action |
|---|---|
| `/bw new` | Create a new room and join it immediately |
| `/bw join <id>` | Switch to room `<id>` (leaves the old one automatically) |
| `/bw leave` | Leave the current room |
| `/bw list` | List active rooms with player counts |
| `/bw info` | Show your current room |

### Admin (console)
| Command | Action |
|---|---|
| `bw.end <id>` | Tear down a room (kills all structures, clears the registry) |
| `bw.listall` | List all rooms with player/entity counts |
| `bw.layers` | Dump all 32 Unity layer slots — which are named, which are free (diagnostic) |
| `bw.testobject.setlayer <0-31>` | [Experiment] set a layer on the object under your crosshair |
| `bw.testexclude <0-31>` | [Experiment] toggle `CharacterController.excludeLayers` — didn't work (no such component), kept as documentation of a dead end |

---

## History: why `BedWars.cs` ended up built this way

Three files preceded this solution (`Ludo_Rooms.cs`, `Ludo_RoomsSpike.cs`, `Ludo_RoomsSpike2.cs`) — an earlier, separate attempt by the same author to solve the same problem using **Unity layers**. What was found there is worth documenting — it's important context.

### `Ludo_RoomsSpike.cs` / `Spike2.cs` — the experimentation phase

These two files are one-off "spikes" (throwaway plugins for testing hypotheses), not a full implementation. Key findings, documented directly in their comments:

- `Physics.IgnoreCollision` does not affect `CharacterController.Move()` — confirmed.
- `Physics.IgnoreLayerCollision` does affect it, but globally (the whole layer matrix, not per-player).
- Free ("unnamed") layers on their Rust build — **only 3**: indices 3, 6, 7.
- Candidate "occupied but seemingly decorative" layers — Bush(26)/Clutter(25)/Physics Debris(31) — were tested separately (Spike2) for repurposing, with the risk of visibly disturbing real bushes/clutter on the map (since the rule is global).
- `Collider.excludeLayers` was tested as a potential per-object alternative to the `IgnoreLayerCollision` toggle.
- Three historical dead ends while trying to hide an entity over the network (documented as ABANDONED #1-3 in `Ludo_RoomsSpike.cs`): kill+respawn (loses state), a hand-crafted destroy packet (doesn't touch the real bookkeeping system), teleporting 1000m away and back (not surgical). The fix was found in someone else's plugin (`Vanish.cs` by Whispers88): `entity.OnNetworkSubscribersLeave(connections)` — the only API that actually updates whatever the engine uses for real visibility bookkeeping.

### `Ludo_Rooms.cs` — a full implementation on layers (ceiling: 1 fully isolated room)

This is a much more developed plugin than `BedWars.cs` in terms of game logic — a full match flow (lobby → countdown → live), a bed system, respawn cooldowns, a CUI scoreboard, arena template scanning, cross-referencing with `Ludo_Markers`. But its approach to physical isolation is fundamentally capped:

```
StructureLayer = 3   // this room's structures
RoomPlayerLayer = 6  // this room's players
// layer 7 is spare, unused
Physics.IgnoreLayerCollision(StructureLayer, standardPlayerLayer, true);
```

Because `IgnoreLayerCollision` is a global, symmetric matrix, making room #1's structures not block FOREIGN players while still blocking its OWN players required moving **both the structures and that room's players** onto dedicated layers. That costs **2 layers per fully isolated room**, and only 3 free layers exist total → **at most one** room gets full (network + physical) isolation; the rest get network isolation only (invisible to non-members, but a foreign player who deliberately walks to that exact spot still physically bumps into it).

The code's own documentation is honest about this limitation:

> "Only 3 free layers exist on this server, so only ONE room can get this - rooms 2+ are visibility-isolated only... UNVERIFIED against other Rust systems (hit detection, NPC/turret targeting) since it moves players off their standard layer"

### A false lead inside `Ludo_Rooms.cs`: `OnAntihackViolation` — actually doesn't work

The `#region Anti-hack exemption inside arenas` section in `Ludo_Rooms.cs` independently arrived at the same root understanding as our own Harmony hunt: the "phantom wall" isn't collision at all — it's server-side anti-cheat validation. The proposed fix looked like a much simpler alternative:

```csharp
private object OnAntihackViolation(BasePlayer player, AntiHackType type, float amount)
{
    if (!_playerRoom.TryGetValue(player.userID, out var slotId)) return null;
    if (type != AntiHackType.NoClip && type != AntiHackType.FlyHack) return null;
    if (Vector3.Distance(player.transform.position, arenaCenter) > arenaRadius + 30f) return null;
    return false; // expected to cancel the kick/rubber-band
}
```

**Checked and disproven.** A comment in `Ludo_Rooms.cs` claimed "Hook confirmed present in this exact build via byte-scan of Assembly-CSharp.dll" — but finding a matching string in the game's own assembly **does not prove** that Carbon actually registers such a hook for plugins. Decompiling `Carbon.Hooks.Oxide.dll` (the real list of hooks Carbon patches in this build) revealed two problems at once:

1. **`OnAntihackViolation` doesn't exist as a hook at all.** The actually registered hook is called **`OnPlayerViolation`**:
   ```
   [Patch("OnPlayerViolation", "OnPlayerViolation", "AntiHack", "AddViolation",
       new string[] { "BasePlayer", "AntiHackType", "System.Single", "UnityEngine.GameObject" })]
   ```
   A plugin method named `OnAntihackViolation` simply isn't a hook Carbon knows about — it never gets called. Dead code.

2. **Even with the correct hook name, this would not have solved the problem.** `OnPlayerViolation` patches the body of `AntiHack.AddViolation(...)`. But the actual position rollback —
   ```csharp
   AddViolation(obj, AntiHackType.NoClip, ..., Colliders[invalidIndex].gameObject);
   if (ConVar.AntiHack.noclip_reject)
       results[invalidIndex] = BasePlayer.PositionChange.Invalid;   // ← this IS the "phantom wall"
   ```
   lives in the **calling** method (`AntiHack.AreNoClipping`), **after** the call to `AddViolation`, not inside it. Cancelling `AddViolation`'s body via the hook does nothing to stop this line from running — the position gets rolled back regardless.

Bottom line: this path would have been non-functional even with the hook name fixed. The only point where the decision is actually made **before** the position rollback is `IsColliderBlocking`, inside `TestAreNoClipping`/`AreNoClipping` — which is exactly where our Harmony patch targets. There is no simpler, official-hook-only path for this specific problem — the investigation confirmed that, it didn't just assume it.

### Comparison summary

| | Layers (`Ludo_Rooms.cs`) | Harmony patches (`BedWars.cs`, this file) |
|---|---|---|
| Max fully-isolated rooms | 1 (capped by 3 free layers) | Unlimited |
| Requires decompiling the game | Yes — the claimed "no-decompilation hook" `OnAntihackViolation` doesn't actually exist and doesn't solve the problem anyway (see above) | Yes |
| Requires moving the player onto a non-standard layer | Yes (risk to hit detection/targeting, never verified) | No |
| Requires a separate Harmony mod / server restart | No | No (after the fix — patches are applied from `Init()`) |
| Building on occupied coordinates | Not solved separately (uses stock `DeployVolume`, unpatched) | Solved (`CheckFlags` + `Vis.Entities<BuildingBlock>`) |

---

## Known limitations of the current implementation (`BedWars.cs`)

- Game logic (commands/beds/matchmaking) is minimal compared to `Ludo_Rooms.cs` — no lobby/countdown/CUI scoreboard, just basic join/leave/end.
- The diagnostic commands `bw.layers`/`bw.testobject.setlayer`/`bw.testexclude` are leftovers from the `CharacterController.excludeLayers` experiment, which didn't pan out (no such component on `BasePlayer`). Kept as documentation of a dead end; can be deleted.
- The Harmony patches target private, internal methods of a specific Rust build (`AntiHack.IsColliderBlocking`, `Construction.UpdatePlacement`, `DeployVolume.CheckFlags`, `Vis.Entities<T>`) — Facepunch can rename or change their signature in any update without warning. If the plugin stops compiling/patching after a game update, `Assembly-CSharp.dll` needs to be re-decompiled to find the current names/signatures of these methods.
- `OnEntityBuilt` covers structures/deployables placed via `Planner`, but not things spawned separately (e.g. door locks, `BaseLock`) — those would need an additional, targeted hook.

---

## How to test this

With a single account, by switching between rooms — the colliders/patches operate per-(object, observer) pair, so switching rooms with the same character is a fully valid test of the mechanism:

```
/bw new                     # room #1, build a wall, don't move
/bw new                     # room #2 (same spot) — the wall should vanish and stop blocking
bw.listall                  # cross-check the registered instance IDs
```
