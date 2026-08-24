using System;

namespace AddressableManager.Editor.Windows.Hub
{
    /// <summary>
    /// How a section or a pipeline stage is doing. <b>Four values, and the fourth is the point.</b>
    /// </summary>
    /// <remarks>
    /// <see cref="NotMeasured"/> exists because its absence is the single most expensive bug pattern
    /// in this package's history. "We checked and found nothing wrong" and "we did not check" were
    /// rendered identically - a green tick, a zero, an empty list - in the Validator, in the build
    /// manifest, in the content-update restriction check and in the Runtime Monitor. A CDN that no
    /// machine had ever successfully reached read as healthy, and a team spent an afternoon
    /// diagnosing the wrong thing on the strength of it.
    ///
    /// So the rule this enum enforces: <b>a check that did not run may never render as a check that
    /// passed.</b> If a value cannot be obtained - offline, no asset, a platform that does not report
    /// it, a probe that threw - the answer is <see cref="NotMeasured"/> with a reason, never
    /// <see cref="Ok"/> and never a silent zero.
    /// </remarks>
    public enum HealthState
    {
        /// <summary>Measured, and fine.</summary>
        Ok = 0,

        /// <summary>Measured, works today, will cost you later.</summary>
        Warning = 1,

        /// <summary>Measured, and it stops the pipeline here.</summary>
        Blocked = 2,

        /// <summary>Not measured. Distinct from <see cref="Ok"/> at every level of the UI.</summary>
        NotMeasured = 3,
    }

    /// <summary>
    /// A section's or stage's health plus the short text the rail shows beside it.
    /// </summary>
    /// <remarks>
    /// A struct with no allocation, because the rail re-derives every badge on each refresh rather
    /// than caching them. Caching a badge is how a stage ends up displaying a verdict that was true
    /// three domain reloads ago.
    /// </remarks>
    public readonly struct SectionHealth : IEquatable<SectionHealth>
    {
        /// <summary>The state.</summary>
        public readonly HealthState State;

        /// <summary>Short text for the rail, e.g. "3 to fix". Never null; empty means "no badge".</summary>
        public readonly string Badge;

        /// <summary>
        /// Why, in one sentence, for a tooltip. Required for <see cref="HealthState.NotMeasured"/>:
        /// "not measured" without a reason is only marginally better than a false green.
        /// </summary>
        public readonly string Reason;

        /// <summary>Create a health value.</summary>
        public SectionHealth(HealthState state, string badge = null, string reason = null)
        {
            State = state;
            Badge = badge ?? string.Empty;
            Reason = reason ?? string.Empty;
        }

        /// <summary>Measured and fine.</summary>
        public static SectionHealth Ok(string badge = null) =>
            new SectionHealth(HealthState.Ok, badge);

        /// <summary>Measured, works, will cost you later.</summary>
        public static SectionHealth Warning(string badge, string reason = null) =>
            new SectionHealth(HealthState.Warning, badge, reason);

        /// <summary>Measured, and the pipeline stops here.</summary>
        public static SectionHealth Blocked(string badge, string reason = null) =>
            new SectionHealth(HealthState.Blocked, badge, reason);

        /// <summary>
        /// Not measured, and the reason is mandatory.
        /// </summary>
        /// <remarks>
        /// The parameter is required rather than optional on purpose. Every caller that reaches for
        /// this has a reason to hand - offline, no CdnSettings asset, play mode not running, the
        /// platform does not report it - and the reason is the whole difference between an honest
        /// "unknown" and a shrug.
        /// </remarks>
        public static SectionHealth NotMeasured(string reason) =>
            new SectionHealth(HealthState.NotMeasured, "—", reason);

        /// <summary>The worse of two states, for rolling section health up into a stage.</summary>
        /// <remarks>
        /// Blocked &gt; Warning &gt; NotMeasured &gt; Ok. NotMeasured deliberately outranks Ok: a stage
        /// with one unmeasured section is not a healthy stage, it is a stage nobody can vouch for.
        /// </remarks>
        public static HealthState Worse(HealthState a, HealthState b) =>
            Rank(a) >= Rank(b) ? a : b;

        private static int Rank(HealthState s)
        {
            switch (s)
            {
                case HealthState.Blocked:     return 3;
                case HealthState.Warning:     return 2;
                case HealthState.NotMeasured: return 1;
                default:                      return 0;
            }
        }

        /// <summary>The USS modifier class for this state, e.g. <c>hub-state--blocked</c>.</summary>
        public string StyleClass => StyleClassFor(State);

        /// <summary>The USS modifier class for a state.</summary>
        public static string StyleClassFor(HealthState state)
        {
            switch (state)
            {
                case HealthState.Ok:          return "hub-state--ok";
                case HealthState.Warning:     return "hub-state--warn";
                case HealthState.Blocked:     return "hub-state--blocked";
                default:                      return "hub-state--unmeasured";
            }
        }

        /// <summary>All modifier classes, so a refresh can remove them all before adding one.</summary>
        public static readonly string[] AllStyleClasses =
        {
            "hub-state--ok", "hub-state--warn", "hub-state--blocked", "hub-state--unmeasured",
        };

        /// <summary>The USS modifier class for a state on an element made of TEXT.</summary>
        /// <remarks>
        /// A separate family from <see cref="StyleClassFor"/> because the two jobs need opposite
        /// things from a background: a dot is its background, a label is destroyed by one. See
        /// <c>HubStyle</c> for which to use where.
        /// </remarks>
        public static string TextClassFor(HealthState state)
        {
            switch (state)
            {
                case HealthState.Ok:          return "hub-text--ok";
                case HealthState.Warning:     return "hub-text--warn";
                case HealthState.Blocked:     return "hub-text--blocked";
                default:                      return "hub-text--unmeasured";
            }
        }

        /// <summary>All text modifier classes.</summary>
        public static readonly string[] AllTextClasses =
        {
            "hub-text--ok", "hub-text--warn", "hub-text--blocked", "hub-text--unmeasured",
        };

        /// <inheritdoc />
        public bool Equals(SectionHealth other) =>
            State == other.State && Badge == other.Badge && Reason == other.Reason;

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is SectionHealth o && Equals(o);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                int h = (int)State;
                h = (h * 397) ^ (Badge != null ? Badge.GetHashCode() : 0);
                h = (h * 397) ^ (Reason != null ? Reason.GetHashCode() : 0);
                return h;
            }
        }
    }
}
