using System;
using System.Globalization;
using System.Threading;

namespace ClearTheWay
{
    /// <summary>
    /// Runs the mod's code under the invariant culture and restores the game's culture after.
    ///
    /// Every number in ClearTheWay.log is formatted by string interpolation or ToString(), and
    /// both use the CURRENT culture: on a German system the log read "speed=69,4 lanePos=-0,23",
    /// so logs from players in other countries differ from ours and every script that reads
    /// them has to guess. Fixing each of the ~85 log
    /// calls would miss the concatenations and ToString() calls in between; switching the
    /// thread's culture for the length of a system update catches all of them in one place.
    ///
    /// Opened with a using declaration at the top of every system's OnUpdate, so early returns
    /// are covered too:
    /// <code>
    ///     using InvariantCultureScope culture = InvariantCultureScope.Enter();
    /// </code>
    /// Only the mod's own code runs inside the scope - no game UI formats text there - and the
    /// previous culture is put back on dispose. Cost: a thread-local read, and two writes only
    /// when the game culture is not already invariant.
    /// </summary>
    internal readonly struct InvariantCultureScope : IDisposable
    {
        private readonly CultureInfo m_Previous;

        private InvariantCultureScope(CultureInfo previous)
        {
            m_Previous = previous;
        }

        public static InvariantCultureScope Enter()
        {
            Thread thread = Thread.CurrentThread;
            CultureInfo current = thread.CurrentCulture;
            if (ReferenceEquals(current, CultureInfo.InvariantCulture))
            {
                return default;
            }
            thread.CurrentCulture = CultureInfo.InvariantCulture;
            return new InvariantCultureScope(current);
        }

        public void Dispose()
        {
            if (m_Previous != null)
            {
                Thread.CurrentThread.CurrentCulture = m_Previous;
            }
        }
    }
}
