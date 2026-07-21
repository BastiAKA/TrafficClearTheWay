using Colossal.Collections;
using Colossal.Mathematics;
using Game.Common;
using Unity.Collections;
using Unity.Entities;

namespace ClearTheWay
{
    /// <summary>
    /// Collects every moving entity whose bounds intersect a query box (a radius around a
    /// crash).
    ///
    /// Crashed vehicles LOSE their CarCurrentLane, so the traffic queued behind an accident
    /// cannot be found by walking lanes - it has to be found spatially, through the game's
    /// moving-object quadtree.
    /// </summary>
    internal struct AreaIterator : INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>, IUnsafeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
    {
        public Bounds3 m_Bounds;
        public NativeList<Entity> m_Results;

        public bool Intersect(QuadTreeBoundsXZ bounds)
        {
            return MathUtils.Intersect(bounds.m_Bounds, m_Bounds);
        }

        public void Iterate(QuadTreeBoundsXZ bounds, Entity item)
        {
            if (MathUtils.Intersect(bounds.m_Bounds, m_Bounds))
            {
                m_Results.Add(item);
            }
        }
    }
}
