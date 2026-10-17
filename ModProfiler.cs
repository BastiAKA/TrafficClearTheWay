using System.Collections.Generic;
using System.Diagnostics;

namespace ClearTheWay
{
    /// <summary>
    /// The passes that can be timed, one bit each in <see cref="ModProfiler"/>'s mask. Names
    /// are written into the log, so keep them recognisable. Append only below 32 values.
    /// </summary>
    internal enum ProfiledPass
    {
        MainSystem,
        PostSystem,
        PathEndGuard,
        Pedestrians,
        EmergencyEscalation,
        CorridorBuilder,
        LaneChange,
        PushVehicles,
        DesperateBehaviour,
        ArrivalAssist,
        AccidentGuard,
        RecoveryAssist,
        TowCoupling,
    }

    /// <summary>
    /// Per-class main-thread profiler for the mod's simulation passes.
    ///
    /// Everything this mod does runs on the SIM THREAD, before or after the vanilla vehicle
    /// systems, so a slow pass shows up as a frame-rate drop with the CPU and GPU both idle
    /// (the post-accident lag hunt was exactly that). This lets a single pass be timed in
    /// isolation without the noise - and the cost - of timing all of them at once.
    ///
    /// HOW TO USE: every profiled class owns its own switch, right at the top of the file,
    /// bound to one <see cref="ProfiledPass"/>:
    /// <code>
    ///     private static bool Profiling => ModProfiler.IsOn(ProfiledPass.CorridorBuilder);
    ///
    ///     using (ModProfiler.Sample(Profiling, "CorridorBuilder"))
    ///     {
    ///         ...
    ///     }
    /// </code>
    /// The switches are set at RUNTIME from the options (Performance tab): one toggle per pass,
    /// plus "all on" / "all off" buttons. Profiling everything at once is the expensive case,
    /// which is why the single-pass toggles exist. Switch one on mid-session, read the <c>[perf]</c>
    /// lines; every change of the set is logged with its frame so a log can be cut there for
    /// an A/B comparison. A new pass gets a <see cref="ProfiledPass"/> value, a toggle in
    /// <see cref="Setting"/>, a line in <see cref="BuildMask"/>, a line in Setting.SetAllProfiled and two
    /// locale entries.
    ///
    /// Cost when the switch is off: one bit test, constructing an empty struct and a null
    /// check on dispose - no timestamp is taken, no dictionary is touched, nothing is logged.
    /// That is why the switch is passed IN rather than checked inside: a disabled sample never
    /// even reads the clock.
    ///
    /// Scopes may nest and may be entered many times per tick; each name accumulates its own
    /// total, worst single entry and call count. <see cref="Report"/> flushes them.
    /// </summary>
    internal static class ModProfiler
    {
        /// <summary>How often the accumulated numbers are logged and reset (sim frames).</summary>
        private const uint kReportIntervalFrames = 300u;

        /// <summary>Bit set of <see cref="ProfiledPass"/> values currently being timed.</summary>
        private static uint s_Mask;

        /// <summary>True when <paramref name="pass"/> is switched on in the options.</summary>
        public static bool IsOn(ProfiledPass pass)
        {
            return (s_Mask & (1u << (int)pass)) != 0;
        }

        /// <summary>
        /// Re-reads the toggles from the options. Called once per tick from the main system,
        /// BEFORE any pass samples, so a toggle takes effect on the next tick without relying
        /// on the options UI calling Apply. Every pass reads the same static mask, so the other
        /// systems need no call of their own.
        /// </summary>
        public static void Refresh(Setting setting, uint frame)
        {
            uint mask = BuildMask(setting);
            if (mask == s_Mask)
            {
                return;
            }
            s_Mask = mask;
            // The cut mark for an A/B comparison: everything before this line was measured
            // with the old set, everything after with the new one.
            Mod.Log.Info($"[perf] profiling set changed at frame={frame}: {Describe(mask)}");
        }

        /// <summary>Number of passes that exist, for the "n of N on" label in the options.</summary>
        public static readonly int kPassCount = System.Enum.GetValues(typeof(ProfiledPass)).Length;

        /// <summary>How many passes are switched on in <paramref name="setting"/>.</summary>
        public static int CountOn(Setting setting)
        {
            uint mask = BuildMask(setting);
            int count = 0;
            for (; mask != 0; mask &= mask - 1)
            {
                count++;
            }
            return count;
        }

        private static uint BuildMask(Setting setting)
        {
            uint mask = 0;
            if (setting == null)
            {
                return mask;
            }
            Add(ref mask, ProfiledPass.MainSystem, setting.ProfileMainSystem);
            Add(ref mask, ProfiledPass.PostSystem, setting.ProfilePostSystem);
            Add(ref mask, ProfiledPass.PathEndGuard, setting.ProfilePathEndGuard);
            Add(ref mask, ProfiledPass.Pedestrians, setting.ProfilePedestrians);
            Add(ref mask, ProfiledPass.EmergencyEscalation, setting.ProfileEmergencyEscalation);
            Add(ref mask, ProfiledPass.CorridorBuilder, setting.ProfileCorridorBuilder);
            Add(ref mask, ProfiledPass.LaneChange, setting.ProfileLaneChange);
            Add(ref mask, ProfiledPass.PushVehicles, setting.ProfilePushVehicles);
            Add(ref mask, ProfiledPass.DesperateBehaviour, setting.ProfileDesperateBehaviour);
            Add(ref mask, ProfiledPass.ArrivalAssist, setting.ProfileArrivalAssist);
            Add(ref mask, ProfiledPass.AccidentGuard, setting.ProfileAccidentGuard);
            Add(ref mask, ProfiledPass.RecoveryAssist, setting.ProfileRecoveryAssist);
            Add(ref mask, ProfiledPass.TowCoupling, setting.ProfileTowCoupling);
            return mask;
        }

        private static void Add(ref uint mask, ProfiledPass pass, bool on)
        {
            if (on)
            {
                mask |= 1u << (int)pass;
            }
        }

        private static string Describe(uint mask)
        {
            if (mask == 0)
            {
                return "off";
            }
            var names = new List<string>();
            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1u << i)) != 0)
                {
                    names.Add(((ProfiledPass)i).ToString());
                }
            }
            return string.Join(", ", names);
        }

        private sealed class Bucket
        {
            public double m_TotalMs;
            public double m_MaxMs;
            public int m_Calls;
            public int m_Ticks;
        }

        private static readonly Dictionary<string, Bucket> s_Buckets = new Dictionary<string, Bucket>();
        private static readonly double s_TicksToMs = 1000.0 / Stopwatch.Frequency;
        private static uint s_LastReportFrame;

        /// <summary>
        /// Opens a timing scope. When <paramref name="enabled"/> is false this is a no-op and
        /// costs nothing measurable. Intended to be used with <c>using</c>.
        /// </summary>
        public static Scope Sample(bool enabled, string name)
        {
            return new Scope(enabled ? name : null);
        }

        /// <summary>
        /// Marks the end of a simulation tick for <paramref name="name"/>, so the report can
        /// show a per-TICK average rather than a per-CALL one. Optional: only call it for
        /// scopes that are entered many times per tick (once per vehicle, say), where the
        /// per-call average alone hides the real per-frame cost.
        /// </summary>
        public static void EndTick(bool enabled, string name)
        {
            if (!enabled)
            {
                return;
            }
            GetBucket(name).m_Ticks++;
        }

        /// <summary>
        /// Logs and resets every accumulated bucket, at most once per
        /// <see cref="kReportIntervalFrames"/>. Call this once per tick from the main system;
        /// it returns immediately when nothing is being profiled.
        /// </summary>
        public static void Report(uint frame)
        {
            if (s_Buckets.Count == 0 || frame - s_LastReportFrame < kReportIntervalFrames)
            {
                return;
            }
            s_LastReportFrame = frame;
            foreach (KeyValuePair<string, Bucket> entry in s_Buckets)
            {
                Bucket b = entry.Value;
                if (b.m_Calls == 0)
                {
                    continue;
                }
                // Per-tick numbers are the ones that map onto frame time; the per-call
                // average tells you whether a pass is slow itself or just called a lot.
                double perTick = b.m_Ticks > 0 ? b.m_TotalMs / b.m_Ticks : b.m_TotalMs;
                Mod.Log.Info(
                    $"[perf] {entry.Key} total={b.m_TotalMs:F2}ms calls={b.m_Calls} " +
                    $"avgCall={b.m_TotalMs / b.m_Calls:F3}ms maxCall={b.m_MaxMs:F2}ms" +
                    (b.m_Ticks > 0 ? $" perTick={perTick:F2}ms over {b.m_Ticks} ticks" : string.Empty));
                b.m_TotalMs = 0;
                b.m_MaxMs = 0;
                b.m_Calls = 0;
                b.m_Ticks = 0;
            }
        }

        private static Bucket GetBucket(string name)
        {
            if (!s_Buckets.TryGetValue(name, out Bucket bucket))
            {
                bucket = new Bucket();
                s_Buckets[name] = bucket;
            }
            return bucket;
        }

        private static void Record(string name, long startTimestamp)
        {
            double ms = (Stopwatch.GetTimestamp() - startTimestamp) * s_TicksToMs;
            Bucket bucket = GetBucket(name);
            bucket.m_TotalMs += ms;
            bucket.m_Calls++;
            if (ms > bucket.m_MaxMs)
            {
                bucket.m_MaxMs = ms;
            }
        }

        /// <summary>
        /// A single timing scope. A null name means "disabled" - Dispose then does nothing.
        /// A struct so an inactive sample allocates nothing on the heap.
        /// </summary>
        internal readonly struct Scope : System.IDisposable
        {
            private readonly string m_Name;
            private readonly long m_Start;

            public Scope(string name)
            {
                m_Name = name;
                m_Start = name == null ? 0L : Stopwatch.GetTimestamp();
            }

            public void Dispose()
            {
                if (m_Name != null)
                {
                    Record(m_Name, m_Start);
                }
            }
        }
    }
}
