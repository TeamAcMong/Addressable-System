using System;
using UnityEditor;

namespace AddressableManager.Editor.Windows.Hub
{
    /// <summary>
    /// Holds a section's health for a few seconds so an expensive check is not run once a second.
    /// </summary>
    /// <remarks>
    /// <see cref="IHubSection.GetHealth"/> says probes must be cheap, and three of the sections
    /// written against it were not: one ran <c>AssetDatabase.FindAssets</c>, one ran
    /// <c>Resources.Load</c> plus a full profile evaluation, and one allocated a list of every cached
    /// asset in every live scope. All three ran on the rail's one-second timer. Writing the rule and
    /// then breaking it three times in the same afternoon is a good argument for a shared answer
    /// rather than three ad-hoc ones.
    ///
    /// <b>What this does to the stability check.</b> <c>HubProbeCLI</c> and
    /// <c>HubSectionContractTests</c> call <c>GetHealth</c> twice and fail if the answers differ. A
    /// throttled probe returns its cached value on the second call, so that check no longer proves
    /// the underlying computation is deterministic - it proves the rail will not flicker, which is
    /// the property those callers actually care about. Said plainly here rather than left for someone
    /// to discover: the test is weaker than it looks, and deliberately so.
    ///
    /// Editor-only, single-threaded, no locking. <see cref="EditorApplication.timeSinceStartup"/>
    /// rather than a Stopwatch because it survives a domain reload the way this window's state does.
    /// </remarks>
    internal sealed class HealthThrottle
    {
        private readonly double _seconds;
        private SectionHealth _last;
        private double _nextAt;
        private bool _hasValue;
        private int _generation;

        /// <summary>
        /// Bumped by <see cref="InvalidateAll"/>. Every throttle compares against it and recomputes
        /// once when it has moved.
        /// </summary>
        /// <remarks>
        /// A counter rather than a list of live throttles: sections are rebuilt on every navigation,
        /// so a registry would need each one to unregister itself on teardown, and a throttle that
        /// outlived its section would keep a dead section's probe alive. A number nobody has to
        /// deregister from cannot leak.
        /// </remarks>
        private static int _globalGeneration;

        /// <summary>Create a throttle. Three seconds is slow enough to be free and fast enough to feel live.</summary>
        public HealthThrottle(double seconds = 3.0)
        {
            _seconds = seconds;
        }

        /// <summary>The cached value, recomputing when it has gone stale.</summary>
        public SectionHealth Get(Func<SectionHealth> compute)
        {
            double now = EditorApplication.timeSinceStartup;

            if (_generation != _globalGeneration)
            {
                _generation = _globalGeneration;
                _hasValue = false;
            }

            if (_hasValue && now < _nextAt)
                return _last;

            // A probe that throws must not poison the cache with a half-value or repeat the failure
            // every tick. It is recorded as unmeasured, with the reason, and retried on the next
            // window rather than immediately.
            try
            {
                _last = compute();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[AddressableManagerHub] A health probe threw: {ex.Message}");
                _last = SectionHealth.NotMeasured("The check for this screen failed to run.");
            }

            _hasValue = true;
            _nextAt = now + _seconds;
            return _last;
        }

        /// <summary>Force the next <see cref="Get"/> to recompute — call after an action changes things.</summary>
        public void Invalidate()
        {
            _hasValue = false;
            _nextAt = 0;
        }

        /// <summary>
        /// Force every throttle in the window to recompute once.
        /// </summary>
        /// <remarks>
        /// Exists so "Re-scan everything" can mean it. Each section owns its own throttle, so without
        /// this the button could only redraw the screen it sits on - from the same three-second-old
        /// answers it was already showing. A control that reports less than its label is the defect
        /// this window was built to remove; it must not be in the window's own header.
        /// </remarks>
        public static void InvalidateAll() => _globalGeneration++;
    }
}
