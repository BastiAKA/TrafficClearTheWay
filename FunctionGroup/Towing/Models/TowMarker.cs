using System.Runtime.InteropServices;
using Colossal.Serialization.Entities;
using Unity.Entities;

namespace ClearTheWay
{
    /// <summary>
    /// Persistent ownership marker placed on every wreck ClearTheWay takes onto its tow hook.
    ///
    /// Why a real component and not the in-memory <c>m_OurTows</c> set: when we couple a wreck we
    /// strip its accident markers (InvolvedInAccident, Moving) so it stops being a managed accident
    /// wreck - which also makes it invisible to every accident-based query. The old ownership signal
    /// was a HashSet, which is EMPTY after a save/load, so a towed wreck whose truck despawned across
    /// a load was never recognised as ours again and stood on the road forever (the "leftover empty
    /// wreck" relics). A tag component travels WITH the entity: it survives our own stripping and,
    /// thanks to <see cref="IEmptySerializable"/>, survives save/load too - exactly the vanilla
    /// pattern for empty persistent tags (e.g. Game.Vehicles.OutOfControl). So our entities are
    /// always findable regardless of which vanilla components come and go.
    ///
    /// Empty tag: presence is the whole payload. Size = 1 (no zero-size chunk component).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 1)]
    public struct TowMarker : IComponentData, IQueryTypeParameter, IEmptySerializable
    {
    }
}
