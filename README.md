# Traffic: Clear the Way — a Cities: Skylines II mod

Emergency vehicles get through, accidents stop deleting your traffic, and crashed cars are
actually towed away.

In vanilla, an ambulance with lights and siren queues at a red light like everybody else, and
the traffic jammed behind a crash simply **evaporates** — on a fully blocked road the game finds
no route past the wreck and quietly despawns the whole queue. This mod fixes the entire chain:
getting there, the accident itself, and the clean-up afterwards.

## Emergency corridor

For every vehicle the game flags as responding (`CarFlags.Emergency`, i.e. siren on), cars pull
to the edge of their lane and a corridor opens (right-hand traffic: cars right, responder passes
left; mirrored in left-hand traffic cities). If that is not enough, the responder escalates:

1. **Two-sided corridor** along its current and upcoming lanes (default 120 m).
2. **Green lights** — signals along the path are petitioned at emergency priority, so the queue
   can clear the crossing. Vanilla only petitions as far as the (stopped ⇒ tiny) braking distance.
3. **Hard evade** — cars move onto the shoulder/parking strip once the responder is really stuck.
4. **Overtake** on a free parallel lane (only when it is significantly freer, or the route needs it).
5. **Oncoming carriageway** — commits only with a 75 m clear gap, holds oncoming traffic while
   crossing, and merges back in once past the queue.
6. **Squeeze past** — sets the game's own `CarLaneFlags.IgnoreBlocker` to creep past a blocker
   that is boxed in itself, gated on ≥1 m of real measured lateral separation.
7. **Arrival assist** — a responder that gets within 30 m of its target but can never reach the
   exact path end (wreck on it, repath moved it) is forced to arrive instead of circling the
   block. Near the target the mod stands down entirely so the vehicle can brake and stop.

Pedestrians about to step onto a crossing wait at the kerb while a responder approaches
(anyone already on the crossing keeps walking, so the road clears). Vanilla only ever stops
pedestrians at a red light — an unsignalled zebra crossing has no signal, so they walk straight
out in front of the ambulance.

## Accidents

- **No despawn behind accidents** — the queue waits for recovery instead of being deleted.
  Several distinct vanilla despawn paths are shielded: truncated path ends, the repath
  broadcast at a blocked lane, and the stuck-traffic cleanup.
- **Approach braking** — cars heading at a wreck slow down early instead of piling into it
  (a secondary crash was a major source of "despawns").
- **Wreck consolidation** — a few seconds after a crash, settled wrecks (including their
  trailers) are moved onto the outermost lane of their side, but only when the whole
  carriageway is blocked. One lane stays pathfind-passable, so traffic queues and squeezes
  past instead of dissolving.

## Vehicle recovery & towing

- **Tow depot** — a buildable depot restricted to vehicle recovery, so its trucks never do
  street or snow work and head straight to the accident.
- **Tow truck** — a dedicated vehicle that **hauls the wreck away** instead of "repairing" it
  on the spot.
- **Dispatch assist** — the nearest capable van gets the job; if none is near, the request goes
  to your tow depot instead of a van from the other end of the map. Whoever holds the order
  drives **directly** to the crash instead of finishing its road-repair round first.
- **Priority** — recovery vehicles get a gentle corridor, amber beacons, and after 30 seconds
  stuck they force their light green and work past the jam.

## Compatibility

No Harmony patches, no replaced game systems, no custom ECS components — the mod only writes
vanilla component fields (`m_LanePosition`, `m_LaneFlags`, `PathOwner`, `CarNavigation` …) from
its own small systems ordered around `CarNavigationSystem` / `CarMoveSystem`. Save games stay
vanilla-compatible and the mod can be added or removed at any time (bulldoze the tow depot first
if you built one). Works with right-hand and left-hand traffic.

## Options (Options → Traffic: Clear the Way)

| Option | Default | Effect |
| --- | --- | --- |
| Enable the mod | on | Master toggle |
| Corridor distance | 120 m | How far ahead cars start pulling aside |
| Squeeze past stopped cars | on | Pass boxed-in blockers once ≥1 m real lateral separation exists |
| Clear undecided lanes | on | On multi-lane roads clear every lane the responder could still pick |
| Overtake on parallel lanes | on | Stuck responders change onto a free neighbouring lane and merge back |
| Green lights for the corridor | on | Signals along the path switch green |
| Use the oncoming lane | on | Cross the centre line when there is a gap |
| Pedestrians wait at the kerb | on | Pedestrians don't step onto a crossing in front of a responder |
| Recovery vehicle priority | on | Dispatch assist, direct route, corridor, beacons, 30 s escalation |
| No despawn behind accidents | on | Queued traffic waits instead of vanishing |
| Clear wrecks onto one lane | on | Consolidate wrecks so one lane stays passable |
| Tow depot building | on | Adds the tow depot to the construction menu |
| Tow wrecks to the depot | on | Wrecks are hauled off instead of evaporating |
| Dedicated tow truck | on | Only the proper tow truck hauls wrecks |
| Verbose logging | off | Diagnostics to `Logs/ClearTheWay.log` |

## Building

```
dotnet build -c Release
```

`GamePath` in the csproj defaults to the Steam install
(`C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II`); override with
`dotnet build -c Release -p:GamePath="D:\...\Cities Skylines II"` if needed.
The build deploys `ClearTheWay.dll` to the game's local mods folder:
`%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\ClearTheWay\`.
A game restart is required to reload a rebuilt DLL.

## Testing in game

Start the game with `-developerMode` to get the debug overlay (Ctrl+Backspace), pick a moving
car and start a traffic-accident event on it. Enable *Verbose logging* to get diagnostics in
`Logs/ClearTheWay.log`.

## Publishing

Publishing to Paradox Mods requires the official modding toolchain (install it in-game via
*Options → Modding*), which provides the ModPublisher tool used by the IDE's Publish action.
Mod metadata lives in `Properties/PublishConfiguration.xml`.
