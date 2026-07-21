using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Vehicles;
using Unity.Entities;

namespace ClearTheWay
{
    /// <summary>
    /// The entity queries the towing passes run on.
    ///
    /// Several of these carry a filter decision that was expensive to get right - which
    /// components to require, and above all which NOT to - so they are kept together with the
    /// reasoning rather than buried in OnCreate.
    /// </summary>
    internal static class TowQueries
    {
        public static EntityQueryDesc TruckQuery => new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadWrite<Game.Vehicles.MaintenanceVehicle>(),
                ComponentType.ReadOnly<Car>(),
                ComponentType.ReadOnly<Target>(),
                ComponentType.ReadOnly<Transform>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.ReadOnly<Owner>()
            },
            None = new[]
            {
                ComponentType.ReadOnly<Deleted>(),
                ComponentType.ReadOnly<Game.Tools.Temp>()
            }
        };

        // Towed wrecks: damaged cars carrying a Controller tow marker. Deliberately does NOT
        // require Stopped and does NOT exclude InvolvedInAccident: after a SAVE/LOAD the game
        // re-initialises the wreck's physics/accident state (it can come back Moving and/or
        // with InvolvedInAccident re-added), and a secondary impact can do the same mid-play.
        // With those in the filter the wreck silently dropped out of this query, the follow
        // pass stopped running for it (so KeepTruckReturning never fired), the AI re-dispatched
        // the truck and the towed vehicle was stranded ("trucks lose their load after loading").
        // FollowOrFinish re-normalises it to the static state and teleports it along. The real
        // ownership guard is NOT the archetype filter but the carrier check in FollowOrFinish
        // (Controller must point at a live MaintenanceVehicle), plus the trailer exclusions
        // below - so loosening Stopped/InvolvedInAccident is safe (a foreign rig is still
        // skipped there). CarTrailer/CarTrailerLane stay excluded: a crashed civilian trailer
        // must never be teleported/deleted by us.
        public static EntityQueryDesc TowedQuery => new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<Car>(),
                ComponentType.ReadOnly<Controller>(),
                ComponentType.ReadOnly<Transform>()
            },
            Any = new[]
            {
                ComponentType.ReadOnly<Destroyed>(),
                ComponentType.ReadOnly<Damaged>()
            },
            None = new[]
            {
                ComponentType.ReadOnly<CarTrailer>(),
                ComponentType.ReadOnly<CarTrailerLane>(),
                ComponentType.ReadOnly<Deleted>(),
                ComponentType.ReadOnly<Game.Tools.Temp>()
            }
        };
        // Orphan sweep: BROADER than m_TowedQuery on purpose - NO Stopped requirement and
        // NO InvolvedInAccident exclusion, so it still catches a towed wreck that fell out
        // of the follow query (a secondary impact re-added InvolvedInAccident, or it lost
        // Stopped). Real trailers stay excluded (CarTrailer/CarTrailerLane). Any match
        // therefore carries OUR Controller tow marker; the sweep only deletes when its
        // carrier is gone or parked (see OrphanSweep).
        public static EntityQueryDesc OrphanQuery => new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<Car>(),
                ComponentType.ReadOnly<Controller>(),
                ComponentType.ReadOnly<Transform>()
            },
            Any = new[]
            {
                ComponentType.ReadOnly<Destroyed>(),
                ComponentType.ReadOnly<Damaged>()
            },
            None = new[]
            {
                ComponentType.ReadOnly<CarTrailer>(),
                ComponentType.ReadOnly<CarTrailerLane>(),
                ComponentType.ReadOnly<Deleted>(),
                ComponentType.ReadOnly<Game.Tools.Temp>()
            }
        };

        // Trailer orphans: before the hookup-time trailer despawn, towing a rig's tractor
        // away left its trailer standing on the road with a Controller pointing at an
        // entity that no longer exists (the wreck was SafeDeleted at the depot). Vanilla
        // always deletes a rig's members together, so a DEAD controller reference only
        // ever means one of our tows stranded the trailer. Any LIVE controller - driving
        // or parked, e.g. a parked civilian car with its trailer still hitched - protects
        // it (that parked case is precisely the session-9 civil-trailer deletion bug).
        public static EntityQueryDesc TrailerOrphanQuery => new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<CarTrailer>(),
                ComponentType.ReadOnly<Controller>(),
                ComponentType.ReadOnly<Transform>()
            },
            None = new[]
            {
                ComponentType.ReadOnly<Deleted>(),
                ComponentType.ReadOnly<Game.Tools.Temp>()
            }
        };

        // Relic finder (diagnostic): a damaged/destroyed car that is NOT currently in an
        // accident. A live wreck carries InvolvedInAccident; once towed we strip it, so a
        // stranded leftover matches here while being invisible to the accident queries.
        public static EntityQueryDesc RelicDiagQuery => new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<Car>(),
                ComponentType.ReadOnly<Transform>()
            },
            Any = new[]
            {
                ComponentType.ReadOnly<Destroyed>(),
                ComponentType.ReadOnly<Damaged>()
            },
            None = new[]
            {
                ComponentType.ReadOnly<Game.Events.InvolvedInAccident>(),
                ComponentType.ReadOnly<Deleted>(),
                ComponentType.ReadOnly<Game.Tools.Temp>()
            }
        };


        // Proposed relic-sweeper signature (DRY RUN - only counted, nothing deleted yet):
        // a damaged/destroyed car still carrying a tow Controller and Stopped, but NOT a live
        // accident wreck (InvolvedInAccident), NOT one of our active tows (TowMarker), not
        // moving, and not a real trailer. That uniquely describes a wreck coupled for recovery
        // and then stranded. Confirm this counts exactly the relics before deleting for real.
        public static EntityQueryDesc RelicSweepDryRun => new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<Car>(),
                ComponentType.ReadOnly<Controller>(),
                ComponentType.ReadOnly<Stopped>()
            },
            Any = new[]
            {
                ComponentType.ReadOnly<Destroyed>(),
                ComponentType.ReadOnly<Damaged>()
            },
            None = new[]
            {
                ComponentType.ReadOnly<Game.Events.InvolvedInAccident>(),
                ComponentType.ReadOnly<TowMarker>(),
                ComponentType.ReadOnly<RelicRecovery>(),
                ComponentType.ReadOnly<Moving>(),
                ComponentType.ReadOnly<CarTrailer>(),
                ComponentType.ReadOnly<CarTrailerLane>(),
                ComponentType.ReadOnly<Deleted>(),
                ComponentType.ReadOnly<Game.Tools.Temp>()
            }
        };
        public static EntityQueryDesc ArmedRelicQuery => new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<RelicRecovery>() },
            None = new[]
            {
                ComponentType.ReadOnly<Deleted>(),
                ComponentType.ReadOnly<Game.Tools.Temp>()
            }
        };
    }
}
