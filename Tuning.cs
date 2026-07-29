namespace ClearTheWay
{
    /// <summary>
    /// Every tuning constant the emergency-corridor passes share, in one place.
    ///
    /// These are the numbers that get adjusted from in-game observation, so they live
    /// together rather than scattered across the classes that happen to read them - and
    /// several are read by more than one pass anyway (the corridor builder, the traffic
    /// shaper and the oncoming pass all reason about the same distances).
    ///
    /// Consumers pull them in unqualified with `using static ClearTheWay.Tuning;`, so call
    /// sites still read kEdgeMeters rather than Tuning.kEdgeMeters.
    ///
    /// Each comment records WHY the value is what it is - usually a bug it fixed. Read it
    /// before changing the number.
    /// </summary>
    internal static class Tuning
    {
        internal const float kPullRate = 0.08f;          // lerp factor per simulation tick while pulling aside
        internal const float kReleaseRate = 0.03f;       // lerp factor per tick when drifting back to center
        internal const float kMinAsidePosition = 0.25f;  // blocker counts as "pulled aside" beyond this
        internal const float kAsideReachedFraction = 0.6f; // ...but a car that was told to go FURTHER is only "aside" once it has covered this much of its own target. The flat 0.25 was calibrated when targets were about one unit; against today's 2-4 unit targets it slowed cars after ~8% of the way, and a car realises its offset only by DRIVING - so it then just steered without translating ("die drehen sich nur, fahren nicht viel raus"). Judge against the target, not a constant.
        internal const float kMaxSqueezeSpeed = 5f;      // emergency must be slower than this (m/s) to squeeze
        internal const float kMaxBlockerSpeed = 3.5f;    // blocker must be slower than this (m/s) to be passed
        internal const uint kReleaseDelayFrames = 120u;  // ~2 sim-seconds without refresh before releasing
        internal const uint kStuckFrames = 300u;         // stuck behind the same blocker this long => squeeze anyway
        internal const uint kSqueezeLatchFrames = 900u;  // once squeezing started, keep it easy for this long
        internal const float kArriveAssistRange = 30f;   // vanilla IsCloseEnough radius around the dispatch target ("has arrived, stop meddling")
        internal const float kArriveOrbitRange = 48f;    // ... but a responder that CANNOT reach the exact path end orbits the block at a radius outside 30 m; count that circling here so arrival still gets forced. TUNE: too wide forces arrival too far from the target.
        internal const uint kArriveAssistFrames = 360u;  // cumulative frames spent within the orbit ring WITHOUT arriving => force arrival (~6 s)
        internal const uint kArriveStallFrames = 240u;    // ... OR trip the arrival as soon as it can get no CLOSER for this long (~4 s). An orbiting responder never stands still (the near arc of its loop is too short to brake in - it circled forever under the speed gate alone), so the real "as near as it will ever get" signal is a stalled approach, not a low speed.
        internal const float kArriveProgressMeters = 3f;  // ... where "closer" means shaving at least this much off its best distance so far (a creep in the jam does not count as progress and reset the stall timer).
        internal const float kArriveStopSpeed = 1f;       // once committed to arriving on foot, "stopped" = slower than this (m/s); only then is the path end snapped.
        internal const float kArriveRollSpeed = 3f;       // committed to the stop but on a junction/turn lane or mid lane change: roll on at this ceiling until a straight kerb lane is under the wheels (never park diagonally across a corner).
        internal const uint kArriveKerbChangeWindow = 600u; // after committing, spend up to ~10 s trying to reach the kerbmost lane before parking in whatever lane the vehicle is in.
        internal const uint kArriveCommitTimeout = 1800u;   // a committed stop that cannot complete in ~30 s (kerb lane never clears, junction roll never ends) is abandoned; the normal machinery resumes and the counters re-arm.
        internal const uint kArrivePrivateStallFrames = 1440u; // the route genuinely ends OFF the street (driveway/parking lot/garage of the target building): give vanilla ~24 s of zero progress before forcing a street arrival - queueing into a drive is slow but usually succeeds.
        internal const uint kLogIntervalFrames = 180u;   // verbose log cadence per vehicle
        internal const float kEvadeRange = 40f;          // cars this close ahead of a stuck emergency evade hard
        internal const float kEdgeMeters = 1.4f;         // SOFT evade: the normal pull-aside distance in metres, for the corridor traffic AND the responder's own hug (lane-unit values are scaled by lane slack). Raised from 1.0 on 2026-07-26 (Sebastian: the soft evade should be somewhat stronger) - at 1.0 a car barely left the lane centre, so a responder arriving at speed still found no gap and the corridor was invisible exactly where it had to work. Still well below kEvadeMeters=1.7, which stays the escalation.
        internal const float kEvadeMeters = 2.0f;        // HARD evade: partially onto the sidewalk / green strip. Raised 1.7 -> 2.0 on 2026-07-26 (Sebastian) once kEdgeMeters went to 1.4, so the soft and hard stages stay clearly apart.
        internal const float kDeepEvadeMeters = 2.8f;    // THIRD push stage: fully onto the pavement. Field report (Azuregen10): on a narrow custom road the column still stood still because 1.7 m leaves a car's body across the gap - the responder never gets through and the whole street freezes. Only reached after kDeepEvadeAfterFrames, so ordinary traffic never sees it.
        internal const uint kDeepEvadeAfterFrames = 1800u; // ~30 s @58 f/s behind the SAME blocker before the deep push engages (kEvadeAfterFrames=150 is the first stage). Deliberately far beyond the normal evade: half a minute of no progress means the standard corridor has demonstrably failed, so the cost of parking cars on the kerb is worth paying.
        // Lateral displacement limit, in METRES and derived per vehicle (GeometryProvider
        // .MaxLateralMeters, cached per prefab). Replaces the old kMaxPushUnits / kArticMaxPushUnits
        // unit caps: those limited metres/slack, and slack floors at 0.5 m, so they bit hardest on
        // the widest vehicles and silently truncated every increase to the stage distances. Three
        // separate bugs in one day came from that shape (RTW pinned at lanePos 2.99, rigs cut off at
        // ~0.7 m, long vehicles collapsing soft into hard). Sebastian: compute it from the length and
        // cache it on the prefab.
        internal const float kLateralPerLength = 0.5f;   // allowance grows with the body: a longer vehicle must travel further sideways before its rear is clear of the corridor
        internal const float kLateralMinMeters = 2.5f;   // ...but never less than this, so an ordinary car can still reach the hard evade plus a little crossable room
        internal const float kLateralMaxMeters = 6f;     // ...and never more, so nothing is flung across a boulevard
        internal const float kArticLateralScale = 0.5f;  // an articulated rig gets half of it: the trailer has no lateral lever and swings out when the tractor is dragged across, narrowing the gap instead of opening it
        internal const float kCrossableRoomCap = 4f;     // most extra metres LateralRoom ever reports for one side, however wide the tram bed / median actually is. A cap, not a target: it stops a car being flung across a boulevard-sized green strip when all we wanted was to free the carriageway.
        internal const uint kLateralRoomRefreshFrames = 64u;  // how long a lane's crossable-room answer is reused (~1 s). Sebastian: query softly, every few frames. Tram OCCUPANCY changes on this timescale, the geometry never does, so this is the tram check's real cadence - and it keeps per-car lane scans out of the per-tick path (the post-accident lag was exactly that kind of sweep).
        internal const uint kLateralRoomPruneFrames = 4096u;  // entries untouched this long are dropped from the room cache
        internal const float kMaxCrossableHeight = 0.35f;    // a composition piece taller than this is treated as a kerb/platform even without the BlockTraffic flag - Sebastian's "solange sie nicht absurd hoch sind". Flat grass, a tram bed and a paved median are all well under this; a raised sidewalk or a tram platform is not.
        // Central-channel corridor for WIDE roads (>= kChannelMinLanes same-direction lanes).
        // Instead of one seam pinned to the far left (where everyone must pile right and, on a
        // packed road, nothing can move), the road parts around a central seam: lanes left of
        // it clear left, lanes right clear right, each only a little. The offset grows toward
        // the edges so the seam actually opens, and all responders on the segment thread the
        // SAME seam (it is computed from the segment, not from the vehicle's own lane), so a
        // cluster of ambulances shares one corridor.
        internal const float kFreeLaneClearMeters = 120f; // how far a lane must be clear to count as FREE and outrank the corridor. The test used kDensityWindow (45 m), which is nowhere near enough to be worth abandoning a formed corridor for - Sebastian: "10 m sind nicht frei, das bringt nix". Matched to the default corridor lookahead, so "free" means open for as far as we plan ahead.
        internal const float kFreeLaneFlowSpeed = 4f;    // in the "is that lane free?" test, a vehicle rolling at least this fast (m/s) is not counted as an obstruction - a flowing lane is one you can join. Sebastian: the query is the problem, there ARE plenty of cars, they are just pulling away (under our own green light) - and counting them made the decision flip every tick.
        internal const uint kFreeLaneLatchFrames = 240u;  // ~4 s: once the responder has picked a free lane to head for, that choice is held this long instead of re-decided every pass. Field case 1901774: lanePos swung -1.3 / +2.4 / -1.7 / +2.2 / +3.9 / -2.3 in seconds because the free-lane test flipped with every car entering or leaving the lane - it never committed to the free lane NOR to the corridor built for it.
        internal const int kFreeLaneReleaseVehicles = 2;  // ...and it is only given up once that many vehicles are actually in the way. Hysteresis: committing needs an EMPTY lane, keeping it tolerates a car or two, so ordinary traffic flow cannot toggle the decision.
        internal const int kChannelMinLanes = 2;         // split the road this way from 2 same-direction lanes up (Sebastian 2026-07-26). Was 3. A genuinely free lane still outranks the channel - see kFreeLaneClearMeters.
        internal const float kChannelBaseMeters = 1.6f;  // offset (m) of the two lanes bordering the seam. Must stay >= kEdgeMeters: it was 1.2 while the soft evade grew to 1.4, so putting a road into channel mode moved its traffic LESS than the plain corridor would have - and at two lanes BOTH lanes border the seam, so they got nothing but this value. On the textbook two-lane case (left lane left, everything else right - the German Rettungsgasse is defined for exactly this) the gap is 2 x this plus whatever shoulder each side can reach, so it has to be worth something on its own.
        internal const float kChannelStepMeters = 0.7f;  // + this much per lane further out (graduated cascade)
        internal const float kChannelShoulderCap = 2.2f; // max extra offset the outermost lane may take onto a shoulder/parking strip
        internal const float kChannelMaxDistance = 45f;  // only part the WHOLE carriageway this far ahead; beyond it the light classic seam (2 lanes/segment) suffices. Without this the channel added every lane of every segment across the full 120 m lookahead - up to ~96 corridor lanes per responder, each an extra PullCarsAside pass.
        internal const float kChannelMaxSpeed = 12f;     // and only part the whole carriageway when the responder is actually SLOW (a cluster/jam) - a free-flowing responder above this needs only the light classic corridor, which skips the many-lane channel work for the many cruising sirens
        // Articulated vehicles (truck/car + trailer): the trailer has no lateral lever and
        // off-tracks (swings out) when the tractor is yanked aside, narrowing the corridor.
        // So drift them gently, less far, never onto the sidewalk, and only while rolling;
        // when stopped/boxed in hold them straight instead of dragging the trailer across.
        internal const float kArticEdgeMeters = 1.2f;    // modest offset for a rig - deliberately below a car's soft kEdgeMeters (1.4), because a trailer cannot realise a big lateral offset without swinging. Raised 1.0 -> 1.2 with the stage values so rigs do not stand out as the one class that no longer moves at all.
        internal const float kArticRateScale = 0.45f;    // gentler lateral rate (× kPullRate) so the trailer tracks in-line
        internal const float kArticRollMin = 1.5f;       // below this speed (m/s) a rig holds straight rather than drifting (a near-stopped lateral drag = max swing)
        internal const float kArticMakeWaySpeed = 3f;     // gentle forward speed floor for a rolling rig ahead of a responder: it keeps easing FORWARD to make way (careful "Platz machen") instead of stalling in the corridor lane. No target override so its heading stays straight and the trailer does not swing.
        internal const float kArticMakeWayRange = 30f;    // only grant the forward make-way to a rig this close ahead of the responder
        internal const uint kBlockerShiftCooldown = 360u; // min frames between forced lane-change attempts for the SAME blocker rig (~6 s) - matches kOvertakeCooldown; guards the pathfinder against a repath flood
        // Anything longer than a car (delivery van, garbage truck, bus) has to go FURTHER aside
        // than a car does. A vehicle does not step sideways - it is pushed diagonally forward, so
        // at ~45 degrees a quarter of its own length is the lateral room it makes: length/4 IS the
        // offset. For a 4.5 m car that is ~1.1 m (about the normal edge offset, so nothing
        // changes); a 6.5 m van reaches 1.6 m and a 9 m garbage truck 2.2 m.
        internal const float kLongVehicleLength = 5.5f;   // longer than this counts as bigger than a car
        internal const float kLongVehicleFraction = 0.25f; // the 45-degree rule: offset = length / 4
        internal const float kLongVehicleMaxMeters = 1.2f; // ... and the BONUS is capped here, so a bus is not parked on the far kerb. Was 2.2 while this value was an absolute floor; as a bonus on top of the stage (1.4 / 2.0 / 2.8) it has to be smaller, or a bus ends up further out than the deep evade ever intended.
        internal const float kCreepSpeed = 1.5f;         // nose-out speed granted to a fully stopped, evading emergency vehicle
        internal const float kClearedSpeed = 7f;         // speed granted once the corridor ahead is genuinely cleared (m/s)
        internal const float kAdvanceClearSpeed = 6f;    // forward floor granted to a responder whose own lane is clear well ahead (no Blocker + no car within kDensityWindow) so it rolls right up to the junction instead of hanging back until the ~10s desperate escalation opens things up
        internal const float kClearedSeparation = 1.6f;  // lateral clearance (m) that counts as "cleared" for the speed boost
        internal const uint kEvadeAfterFrames = 150u;    // slow behind the same blocker this long => start hard evade
        internal const float kMinSqueezeSeparation = 1.0f; // actual lateral distance (m) required before passing a blocker
        // A STANDING recovery vehicle is the one blocker that will never drive off by itself and
        // that our corridor used to leave untouched, so a colleague behind it waited forever.
        // Both are stationary while this is used, so a tighter clearance is safe here - and it is
        // the difference between the chain opening and three trucks timing out in a row.
        internal const float kColleagueSqueezeSeparation = 0.7f;
        // Last resort for a recovery vehicle that has been deadlocked past its give-up: remove the
        // ONE vehicle directly in its way. Proven by hand in-game - deleting a single car in front
        // of the leading tow truck triggered the route recompute that got the whole column moving
        // again. Retried at this interval so a still-blocked truck eventually works through a
        // second blocker, but never every tick.
        internal const uint kUnblockRetryFrames = 600u;   // ~10 s between removals per truck
        internal const float kUnblockMaxSpeed = 0.5f;     // only a blocker that is genuinely standing
        internal const float kRelicRearmTruckRange = 500f; // a stranded wreck still carrying our TowMarker is re-armed for recovery only once NO actively-towing truck is within this many metres - i.e. the tow that owned it has clearly left, so nothing is coming and the marker must be released
        internal const uint kOrphanStrandedFrames = 1200u; // log-only orphan finder REPORT threshold (~20s @58f/s): a damaged/out-of-control wreck that has not physically moved this long is reported as a stranded orphan. Movement is the whole test - a wreck being hauled is teleported every frame so its timer keeps resetting, a stranded one's does not. Deliberately low for the detection phase so we SEE everything and can pick the real (higher) acting threshold from the logged strandedFor values.
        internal const float kOrphanMoveEpsilon = 2f;      // a wreck must move more than this many metres between finder passes to count as "still being hauled" and reset its stranded timer
        internal const uint kOrphanDeleteFrames = 36000u;  // ~10 min @58 f/s (Sebastian's rule): a wreck that has not moved a metre in this long is not going to be recovered by anything - no truck reached it, no release freed it - so it is deleted as the terminal state. Ten minutes is deliberately far longer than vanilla's own 14400-frame accident timeout, so every normal recovery has finished long before this fires.
        internal const uint kFrozenReleaseGraceFrames = 600u; // ~10 s @58 f/s: how long a car must sit Stopped+OutOfControl with no damage and no live accident before the release pass frees it. NOT a wait for it to sort itself out - that state is permanent (ClearAccident alone ever removes OutOfControl, and it cannot reach a car that no longer carries InvolvedInAccident). Purely a guard against acting on a car mid-transition, while another system's command buffer still holds the rest of its changes.
        internal const float kMaintenancePushMaxSpeed = 0.5f; // ...and a recovery vehicle is only ever pushed aside below this speed (a rolling convoy still keeps its line)
        internal const float kHoldRange = 15f;             // cars this far ahead of a squeezing emergency hold still
        internal const float kHoldSpeed = 0.3f;            // ... at most this speed (m/s), and only once it is actually aside (kMinAsidePosition)
        internal const float kMinCreepSpeed = 1.0f;        // traffic in front of a responder is never frozen to a dead stop - it always keeps at least this much forward budget, so a held queue keeps draining and nothing gets permanently stuck (a ceiling still lets the car's own navigation stop it when the car AHEAD of it cannot move, so it only creeps into real space)
        internal const float kBehindRange = 10f;           // cars alongside/just behind still count as being overtaken
        internal const float kProtectStuckRange = 25f;      // clear the stuck flag on any car within this range of the maneuver (anti-despawn)
        internal const float kPassCeilingSpeed = 4f;       // #6: while actively passing, cars being drawn level with are held to at most this (gentle, not a dead stop) so the EV clears them
        internal const float kPassWindowAhead = 22f;       // ... targeting cars up to this far ahead of the EV along the corridor
        internal const float kPassWindowBehind = 4f;       // ... down to a little behind its nose
        // Tombstones proved HOW the queue vanishes: the wreck lanes get PathfindUpdated →
        // queue cars turn Obsolete → REPATH → destination unreachable (full block on a
        // one-way) → the pathfinder hands out a short disposal path into the nearest
        // building connection → CheckUnspawned evaporates the car mid-drive (seen as
        // "Route verschwindet, dann verschwindet das Auto", tgtDist 1170/3013 m). The fix:
        // keep the ORIGINAL (still valid) route alive by clearing Obsolete|DivertObsolete
        // near the accident every frame - cars then physically queue via the Blocker logic
        // and simply WAIT. Once the wrecks are gone the shield lifts and repaths resume.
        internal const float kAccidentQueueRange = 300f;    // protect/shield vehicles within this range of a crashed vehicle
        internal const uint kWreckHoldAge = 7200u;          // hold a secured, uncleared wreck at this age so its delete deadline (InvolvedFrame+14400) stays ~this far ahead
        internal const uint kWreckHoldAgeBike = 200u;       // bicycles/motorcycles are deleted at InvolvedFrame+300 (not 14400!) once secured - hold them younger than that
        // Give-up despawn: if a settled wreck still has not been recovered after this long, stop
        // waiting and let the GAME remove it cleanly (back-date it past the vanilla delete
        // deadline; AccidentVehicleSystem then clears the wreck + accident icon properly). Covers
        // the cases a tow never resolves: no truck ever comes, the wreck is unreachable, or it
        // keeps burning (a tow truck cannot couple a vehicle that is OnFire, so it waits forever).
        // ~58 sim-frames/sec here, so 18000 ≈ 5 min. TUNE: lower = tidier but risks despawning a
        // wreck a slow tow truck is about to reach; higher = wrecks linger longer.
        internal const uint kWreckGiveUpAge = 36000u;         // ~10 min: an UNCLAIMED wreck (no recovery vehicle dispatched) is held this long before it is let despawn. Was 18000 (~5 min) - accidents cleared too fast.
        internal const uint kWreckClaimedGiveUpAge = 108000u; // ~30 min: once a recovery vehicle has been dispatched to the wreck (its request carries a Dispatched handler) the despawn is effectively paused - held until the tow arrives - with only this generous absolute cap as a safety net against a permanently wedged recovery.
        internal const uint kVanillaDeleteAge = 14400u;     // AccidentVehicleSystem deletes a car wreck at InvolvedFrame + this (bicycles: 300)
        // Freeze the pile: wrecks keep OutOfControl+Moving and jiggle from mutual collision
        // impulses. ObjectCollisionSystem sweeps OutOfControl objects and a jiggling wreck
        // that overlaps the queue head fires a flat severity-10 Impact into it (ImpactSystem
        // → InvolvedInAccident + OutOfControl) - the car gets physics-yeeted, falls off the
        // road/bridge (y<-1000 → instant delete) or becomes the next wreck: the chain
        // reaction that looked like "queue despawns at the accident". Zeroing slow wrecks'
        // velocity every frame stops the drift/jiggle and lets the game's own settle check
        // (velocity² < threshold) put them to rest properly.
        internal const float kWreckFreezeSpeed = 1.5f;      // only clamp wrecks slower than this (a car still genuinely crashing/flying is faster - leave its physics alone)
        // Clear wrecks onto ONE lane: pathfind treats a blocked lane stretch as HARD
        // impassable (PathfindJobs.IsValidDelta), so a full block on a one-way makes every
        // destination behind it unreachable → every repath returns a disposal path into the
        // nearest building connection where the car unspawns ("verdampft"). No flag-clearing
        // can fix that. The only real cure: physically consolidate settled wrecks onto the
        // outermost lane of their side so at least one lane stays pathfind-passable and
        // traffic squeezes past - which is also just what real accident clearing looks like.
        internal const uint kWreckClearDelay = 450u;        // settle time before a wreck is moved aside (~7 s)
        internal const float kWreckClearSearchRadius = 20f; // how far around the wreck to look for its road edge
        // Approach-brake: slow traffic that is heading straight at a wreck so it stops before
        // rear-ending it (the real cause of the despawn - cars crash into the pile, become
        // wrecks, get cleared). A distance-based speed ceiling = a smooth braking ramp.
        internal const float kBrakeRange = 60f;             // start braking when a wreck is within this forward distance (short so the straight-line test holds on highway curves)
        internal const float kBrakeSafeGap = 10f;           // come to a stop this far before the wreck
        internal const float kBrakeSpeedPerMeter = 0.35f;   // allowed speed = (forwardDist - safeGap) × this (m/s per m)
        internal const float kBrakeLateral = 5f;            // only brake when the wreck is roughly in the car's path (this close to its heading line), not on a free neighbour lane
        internal const float kBrakeMinSpeed = 0.1f;         // minimum speed for a valid travel direction (crawling queue heads MUST be braked too - they creep into the jiggling pile and get infected)
        internal const float kSidestepSpeed = 0.8f;        // standing overtaken cars may move this fast toward their evade spot
        internal const float kMaxEvadeSpeed = 8f;          // hard evade only below this own speed (m/s)
        internal const float kPassEvadeMeters = 2.5f;      // while actively passing a blocker (squeeze latched / IgnoreBlocker) the vehicle swings out this far - visibly clear of the car it overtakes instead of scraping along it, and the separation gate opens sooner
        internal const uint kHugHoldFrames = 120u;         // corridor hug stays engaged this long (~2s) after the last actual push - `pushed` flickers 0<->1 with distant cars, and gating the hug on it directly made the vehicle weave a metre left/centre at speed
        internal const float kHugTaperStartSpeed = 10f;    // above this own speed the hug gets shallower...
        internal const float kHugTaperRange = 20f;         // ...linearly down to the minimum at start+range: fast traffic ahead has time to part, the vehicle only shades left instead of riding the median
        internal const float kHugMinScale = 0.35f;         // floor of the hug speed taper
        internal const float kMaxOncomingSpeed = 2.5f;     // center-line crossing only below this own speed (m/s)
        internal const uint kOvertakeCooldown = 360u;      // min frames between forced lane changes (~6 sim-seconds)
        internal const uint kDesperateFrames = 600u;       // stuck this long => cross over even against oncoming traffic
        internal const float kOvertakeClearAhead = 30f;    // parallel lane must be free this far ahead to overtake
        internal const float kOvertakeClearBehind = 8f;    // ... and this far behind
        internal const float kTakeoverClearAhead = 8f;     // ... but a genuinely stuck responder (relaxDensity) only needs THIS small a slot to commit to the lane - the slot-opener (OpenLaneChangeSlot) then widens it. The 30 m rule never passes in a packed jam, which is why "lane takeover" did not assert there.
        internal const float kTakeoverClearBehind = 4f;
        internal const float kOvertakeMinRemaining = 25f;  // don't start an overtake this close to the lane end
        internal const uint kForcedChangeTimeout = 600u;   // give up a forced lane change after ~10 sim-seconds
        internal const float kMergeCommitSpeed = 4f;      // forward floor granted to a responder that is MID LANE CHANGE into the corridor, so it completes the move instead of stalling half in it. Every other floor is gated on m_ChangeLane == null, which left the entry into the gap - the one moment a responder most needs to keep rolling - with no support at all: 265540 came to a dead stop at sep=4.3 with the corridor perfectly formed, then slid back out of it (lanePos 1.50 -> 0.85). The 15 s defreeze is far too late to feel like anything but hesitation.
        internal const float kMergeCommitSeparation = 2.5f; // ...but only with at least this much room to whatever is in front, so the floor can never push it into a car it is genuinely queueing behind.
        internal const float kTowStuckMeters = 3f;        // a hauling truck that has not moved further than this counts as motionless. Movement, not path state, is the test: a truck wedged inside a building may carry no Failed flag at all, so the path-based timer never even started (truck 907641 sat in a multi-storey car park with its load for minutes, invisible to all three cleanup nets).
        internal const uint kTowStuckFrames = 900u;       // ~15 s motionless while hauling => wedged. Long enough that an ordinary traffic jam or a red light never trips it, short enough that a truck in a building is not a monument.
        internal const float kTowOnRoadMeters = 5f;       // a wedged truck with a driving lane this close is standing ON the road, i.e. queueing rather than stuck in the geometry - and then nothing in the tow code touches it. Measured in 3D against the lane curve, so a truck a storey above a street still counts as off-road. Half a lane width plus a margin: wide enough that a truck parked at the kerb of its own lane is recognised, narrow enough that a car park interior next to a street is not.
        internal const uint kTowWedgeStrikes = 20u;       // ...but a truck that has been counted this many times (20 x kTowStuckFrames = ~5 min without moving 3 m) is not queueing either. Then its wreck goes back to the recovery pipeline and the truck is deleted - the deliberate choice of losing one vehicle over leaving a loaded monument that blocks every cleanup net behind it.
        internal const uint kTowPathGiveUpFrames = 600u;  // ~10 s: how long a LOADED tow truck may go on without a usable route home before we let go of it. The shield that stops vanilla deleting it also leaves it routeless, and a routeless vehicle drives straight on - across parks and over rooftops, which is exactly what turned up scattered all over Sebastian's map. Shielding therefore has to be bounded: past this, the wreck is released back into the recovery pipeline (it keeps its own watchdogs) and the truck is handed back to its AI, which may well delete it - better one truck than a convoy flying through the city.
        internal const uint kTowPathRetryFrames = 120u;   // ~2 s: how often a recovery vehicle whose route failed may ask for a NEW one. The first request is immediate (PathShield) - the vehicle is routeless the moment its path fails and drives straight on until it has one, so every frame of delay is a frame of driving blind, over kerbs and through parks. Only the REPEATS are throttled, because repathing every tick is what once flooded the pathfinder and despawned unrelated vehicles. Was 256 with no immediate first try, which meant ~128 frames blind on average and another 256 after each further failure.
        internal const uint kDefreezeStuckFrames = 900u;   // stopped mid lane-change this long => trigger a safe recompute (flag lane Obsolete)
        internal const uint kDefreezeCooldown = 900u;      // ... and re-trigger it at most once per this many frames per vehicle
        internal const uint kSacrificeShieldWindow = 900u; // once a hard-blocked responder flags the civilian car directly ahead Obsolete (last-resort plug removal), that car is exempted from NearWreckProtection's keep-alive for this many frames (~15s) so the game's own repath-fail despawn can actually remove it and free the queue behind. If it survives the window (its route WAS routable) it is protected again like any other car.
        internal const uint kSqueezeGiveUpFrames = 360u;   // squeezing the same blocker this long at ~0 speed => stop squeezing, overtake around it (wide/non-aside blocker, e.g. a truck) - ~6s: the corridor + squeeze get a real chance first (at 3s this fired constantly and tore up working corridors), but still before the ~10s oncoming-crossover escalation
        internal const float kTurnApproachDist = 35f;      // within this distance of an upcoming turn: half-merge toward the turn side instead of a forced lane change
        internal const uint kPushClaimFrames = 120u;       // a car pushed one way stays direction-claimed this long (~2s) - overlapping corridors of nearby responders may not push it the opposite way (kills the left/right jitter when several respond together)
        internal const float kOvertakeSlowSpeed = 8f;       // only consider overtaking below this own speed (slow traffic)
        internal const float kDensityWindow = 45f;          // look this far ahead when comparing lane occupancy
        internal const int kDensityAdvantage = 2;           // target lane must have at least this many fewer cars ahead
        internal const sbyte kSignalPriority = 108;        // emergency priority for traffic-light petitions
        internal const uint kLightOverrideFrames = 10800u; // the "German 3-minute rule": once the responder has sat at a signalised junction this long (~3 min) the light is treated as broken - the corridor lanes are hard-forced to Go (bypassing the phase logic) and the queue in front is pushed through, so a dead junction can never trap it forever
        internal const uint kShakeMask = 0xFFu;            // stuck-jam "shake": around a genuinely stuck responder, a STOPPED car is flagged Obsolete once per (mask+1)=256 frames (staggered per car by index) so it RE-EVALUATES its route on the async pathfinder - a stopped CS2 car does not re-path on its own once its way clears (a TTE signal change is what usually animated them). The stagger keeps it a trickle: near-zero main-thread cost, no pathfinder flood.
        internal const float kLightBreakSpeed = 3.5f;      // forward speed granted to the queued cars ahead while breaking them through a stuck light
        internal const float kLightBreakRange = 40f;       // push the queued cars this far ahead of the responder through the junction
        internal const sbyte kAssistSignalPriority = 106;  // recovery-vehicle petitions: beat civilian (100), yield to emergency (108)
        internal const uint kAssistStuckFrames = 1800u;    // recovery vehicle making no progress this long (~30 s) => escalate
        internal const float kAssistProgressMeters = 12f;  // ... where "progress" means getting THIS much closer to the wreck. Wall-clock standstill is useless as a measure: in a stop-and-go jam the vehicle inches forward constantly, and an instantaneous speed check reset the timer on every creep, so the escalation never fired once.
        // RETIRED 2026-07-29: kTowReturnSearchRadius (120 m) belonged to TowStuckRecovery.PutBackOnRoad,
        // which teleported a wedged tow truck onto the nearest road and rewrote its CarCurrentLane by
        // hand. That bypasses the game's lane registry and hard-crashes a Burst job, and in practice it
        // fired on trucks that were merely queueing (904 of 914 recoveries landed "0m away"). The method
        // is gone; a wedged truck is now either left alone or deleted. The old code is parked on branch
        // tow-putbackonroad.
        // RETIRED 2026-07-26: kAssistHelpSpeed (7 m/s) gated the recovery corridor on SPEED, so any
        // maintenance van crawling in ordinary traffic shoved the cars around it aside on sight.
        // Sebastian: they should drive along normally unless actually stuck. RecoveryAssist now
        // gates on stuckLong alone (progress-based, kAssistStuckFrames + kAssistEscalateRange).
        internal const float kAssistEscalateRange = 200f;  // ... and the harsh escalation (evade+squeeze+green) ONLY within this distance of the wreck. Straight-line distance is no progress measure across a whole city - a route that curves away increases it - so trucks 1.7 km out were permanently "stuck" and shoved traffic onto the kerb along their entire route.
        internal const uint kAssistGiveUpFrames = 5400u;   // ... and once even escalation buys NO progress for this long (~90 s), the recovery is wedged in an unwinnable deadlock (a vanilla gridlock the toolkit cannot break - one truck escalated 364x over 152 s stuck at a fixed 30 m). Stop escalating: it only churns surrounding traffic for nothing, and the wreck despawns via give-up @kWreckGiveUpAge anyway. Logged in full detail so the deadlock itself could be tackled later.
        internal const float kOncomingMinOffset = 1.5f;    // candidate oncoming lane must be at least this far to the side
        internal const float kOncomingMaxOffset = 5.5f;    // ... and at most this far (no crossing over medians)
        internal const float kOncomingMergeSight = 50f;     // hold oncoming vehicles within this distance while the body is out there
        internal const float kOncomingCommitSight = 75f;    // to START using the oncoming lane the gap ahead must be at least this large
        internal const float kOncomingClearMargin = 1.2f;   // keep oncoming traffic waiting until the vehicle is physically this close to its lane
        internal const uint kOncomingStickyFrames = 90u;    // once committed to the oncoming lane, stay committed at least this long (anti-wobble)
        internal const float kOncomingStallMeters = 6f;     // a committed pass must gain at least this much ground along the lane...
        internal const uint kOncomingStallFrames = 600u;    // ...per ~10s watch window - otherwise it is going NOWHERE (red junction / blocked continuation) and merges back instead of bouncing on the oncoming side forever
        internal const uint kOncomingRetryBlockFrames = 900u; // after a stalled pass merged back: no new oncoming commit for ~15s (give the normal machinery - corridor, green petition, squeeze - a turn)
        internal const float kConvoyAheadRange = 20f;       // another emergency vehicle within this distance ahead on the same lane => queue behind it (no hard evade / wide swing / new oncoming commit) instead of fanning out across the road
        internal const float kOncomingHardMergeSight = 25f; // an oncoming vehicle this close forces an immediate merge (safety, no debounce)
        internal const uint kMergeDebounceFrames = 45u;     // a soft merge reason must hold this long before merging (rides over queue gaps)
        internal const float kOncomingPassSpeed = 10f;      // brisk speed granted while passing on the free oncoming lane (m/s)
        internal const float kOncomingRunClear = 45f;       // run at pass speed only when the oncoming lane is clear at least this far ahead
        internal const float kOncomingCrawlSpeed = 3f;      // otherwise ease up to the (held) oncoming car ahead at this speed instead of aborting
        internal const float kMergeGapRange = 20f;          // nudge the few cars just ahead of the merge point within this range
        internal const float kMergeGapSpeed = 3.5f;         // ... forward at this speed, opening a gap to slot back in
        internal const int kMergeGapCars = 4;               // at most this many cars are nudged forward

        // --- Towing ---
        internal const float kHookupRange = 32f;    // couple when the truck is this close to its wreck target (vanilla stops/works from up to 30 m away)
        internal const float kHookupMaxSpeed = 3f;  // ... and this slow (it just pulled up / stopped)
        internal const float kHitchDistance = 2.8f; // drawbar coupling point, behind the truck center (~rear bumper of a maintenance van)
        internal const float kTrailDistance = 1.7f; // rope length: wreck center trails this far behind the coupling point (~4.5 m behind truck center = ~1-2 m gap behind the rear)
        internal const float kFollowRate = 0.3f;    // per-tick lerp toward the point behind the truck (lower = smoother, higher = tighter/jumpier)
        internal const uint kRelicRecoveryTimeout = 18000u; // ~5 min: a re-armed relic nothing recovers in this time is deleted as a fallback
        internal const uint kInterval = 32u;          // dispatch pass cadence in frames (~0.5 s)
        internal const float kNearVanRange = 400f;    // a suitable van this close counts as "nearby"
        internal const float kMuchCloserFactor = 0.5f;// a van at less than half the responder's distance takes over
        internal const float kTakeoverMinGainMeters = 120f; // ...but it must ALSO be at least this many metres closer. Halving a distance is a relative test, so at short range it fires on a trivial gain (90 m -> 40 m) and the truck already on its way is cancelled for nothing. Field case 2026-07-26: van 2270668 abbestellt kurz vor Ankunft, und der Van, der uebernahm (2270665), war selbst 18 s zuvor heimgeschickt worden - fuenf "sent home" in drei Minuten, weil in einem Wrack-Cluster jeder Pass einen anderen "naechsten" Van findet. Distances are straight-line, so a marginal gain is not even reliably a real one.
        internal const uint kTakeoverGraceFrames = 1800u;  // ~30 s: a van that was assigned this recently is never taken over. Gives it time to actually get there instead of being re-decided every pass; without it the cluster case above cascades, because each van sent home is immediately free to become the "nearest" for the next wreck.
        internal const float kHandsOffRange = 50f;    // responder this close to the wreck is never disturbed
        internal const uint kActionCooldown = 900u;   // min frames between dispatch interventions per wreck (~15 s)


        //Dead -Opus comment it out pls

        // ---------------------------------------------------------------------------------
        // Depricated - no longer read by any pass. Kept commented rather than deleted because
        // each records a value that was tuned in-game, and the reason it stopped being needed
        // is usually the interesting part:
        //   kArriveAssistMaxSpeed  - superseded by the arrival kerb-stop rework (commit-latch
        //                            + progress watch), which decides when to stop by stall
        //                            rather than by a speed gate.
        //   kEdgePosition /
        //   kEvadeEdgePosition     - the pull-aside switched from fixed lane-position units to
        //                            METERS scaled by the lane's actual lateral slack
        //                            (kEdgeMeters / kEvadeMeters), which behaves on narrow roads.
        //   kOncomingHardBlock /
        //   kOncomingMovingSpeed /
        //   kOncomingWindowAhead /
        //   kOncomingWindowBehind /
        //   kOncomingStraddle      - replaced by the commit/merge hysteresis model
        //                            (kOncomingCommitSight, sticky frames, merge debounce),
        //                            which fixed the dart-out-and-back wobble.
        //   kMaxOvertakeSpeed      - overtaking is now gated on kOvertakeSlowSpeed plus lane
        //                            density, not on a hard own-speed ceiling.
        //   kFarLeftThreshold      - the "outside own lane" test now uses the physical offset.
        //   kWreckExtendCap        - the wreck lifetime cap became the give-up despawn
        //                            (kWreckGiveUpAge / kWreckClaimedGiveUpAge).
        // ---------------------------------------------------------------------------------        // float kEdgePosition = 0.45f;      // target |m_LanePosition| for cars pulling aside
        // float kArriveAssistMaxSpeed = 4f; // ... but never force it at speed: the snapped path end puts the nav target BESIDE a moving car, it swerves hard and StopVehicle freezes it mid-rotation (police standing across the lane). Vanilla's own close-arrival only ever fires on a standing, blocked car. So once the counter is up we brake the vehicle to this ceiling first and force the arrival when it is actually slow.
        // float kEvadeEdgePosition = 0.95f; // hard-evade target: partially onto sidewalk/parking lane
        // uint kWreckExtendCap = 43200u;       // ... but stop extending once the wreck has truly waited this long since first seen (avoid a permanent blockage if no responder ever comes)
        // float kFarLeftThreshold = 1.2f;     // |lanePosition| beyond this = passing outside the own lane
        // float kOncomingHardBlock = 10f;     // oncoming vehicle closer than this always blocks full crossover
        // float kOncomingMovingSpeed = 3f;    // oncoming vehicle faster than this blocks full crossover
        // float kMaxOvertakeSpeed = 1.5f;     // forced lane change only below this own speed (m/s)
        // float kOncomingWindowAhead = 35f;   // oncoming lane must be free this far ahead to fully cross over
        // float kOncomingWindowBehind = 6f;   // ... and this far behind
        // float kOncomingStraddle = 2.2f;     // fallback offset (m) straddling the center line while waiting for a gap
    }
}
