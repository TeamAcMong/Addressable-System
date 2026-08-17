using System;

namespace AddressableManager.Pooling
{
    /// <summary>
    /// Configuration for dynamic pool behavior with auto-sizing
    /// </summary>
    [Serializable]
    public class DynamicPoolConfig
    {
        /// <summary>
        /// The auto-resize controller's STARTING CAPACITY BUDGET — <b>not</b> a number of instances
        /// to create (discovery report P-18).
        /// </summary>
        /// <remarks>
        /// Nothing is instantiated because of this value. It is the initial denominator of
        /// <c>usageRatio = activeCount / capacity</c>, which is what <c>DynamicPool</c>'s
        /// <see cref="GrowThreshold"/> / <see cref="ShrinkThreshold"/> are compared against, and the
        /// value <c>DynamicPoolStats.CurrentCapacity</c> reports. A pool created with the default
        /// config and no <c>preloadCount</c> holds <b>zero</b> instances while reporting
        /// <c>CurrentCapacity == 10</c>, and the first growth therefore does not trigger until 8
        /// instances are concurrently checked out.
        ///
        /// To actually pre-populate a pool, pass <c>preloadCount</c> to
        /// <c>AddressablePoolManager.CreateDynamicPoolAsync</c> / <c>CreatePoolAsync</c>, or call
        /// <c>AddressablePoolManager.PrewarmPool</c> afterwards.
        ///
        /// Whether this field should instead prewarm (making the name true) or be renamed to
        /// something like <c>InitialCapacityBudget</c> is recorded in
        /// <c>Documentation/LIFETIME_DESIGN.md</c> under "Pooling: decisions still open" — it is a
        /// product decision, because prewarming by default would silently start instantiating ten
        /// GameObjects per pool at creation time.
        /// </remarks>
        public int InitialCapacity = 10;

        /// <summary>
        /// Floor for the auto-resize controller's capacity budget — <c>DynamicPool</c> will not
        /// shrink <see cref="InitialCapacity"/>'s running value below this.
        /// </summary>
        /// <remarks>
        /// P-18: like <see cref="InitialCapacity"/> this bounds the budget, not the population. An
        /// explicit <c>TrimExcess</c>/<c>ShrinkPool</c> call from a caller can still empty the free
        /// list completely; <c>MinSize</c> only gates the automatic path.
        /// </remarks>
        public int MinSize = 5;

        /// <summary>
        /// Maximum pool size (hard limit). Must be at least 1.
        /// </summary>
        /// <remarks>
        /// P-19: <b>0 does not mean "unlimited" here</b>, unlike the <c>maxSize</c> parameter on
        /// <see cref="IPoolFactory.CreatePool"/> / <c>AddressablePoolManager.CreatePoolAsync</c>,
        /// where P-7 standardised <c>&lt;= 0</c> as unlimited. The two conventions are opposites and
        /// they meet where <c>CreateDynamicPoolAsync</c> passes this value straight into the factory:
        /// a <c>MaxSize</c> of 0 used to produce a pool with growth permanently disabled
        /// (<c>_currentCapacity &lt; MaxSize</c> is <c>0 &lt; 0</c>) sitting on top of an
        /// <i>unbounded</i> free list that retained every released instance forever — the exact
        /// opposite of the "hard limit" this field documents. <see cref="Validate"/> now rejects it
        /// rather than let the inversion happen silently.
        /// </remarks>
        public int MaxSize = 100;

        /// <summary>
        /// When active count reaches this % of capacity, grow the pool
        /// Range: 0.0 - 1.0 (e.g., 0.8 = 80%)
        /// </summary>
        public float GrowThreshold = 0.8f;

        /// <summary>
        /// When active count drops below this % of capacity, consider shrinking
        /// Range: 0.0 - 1.0 (e.g., 0.3 = 30%)
        /// </summary>
        public float ShrinkThreshold = 0.3f;

        /// <summary>
        /// How much to grow the pool when threshold is reached
        /// Range: 0.0 - 1.0 (e.g., 0.5 = grow by 50%)
        /// </summary>
        public float GrowFactor = 0.5f;

        /// <summary>
        /// How much to shrink the pool when below threshold for extended period.
        /// Range: greater than 0.0, up to 1.0 (e.g., 0.25 = shrink by 25% of the excess).
        /// </summary>
        /// <remarks>
        /// P-20: 0 is rejected by <see cref="Validate"/>. It used to pass validation and then shrink
        /// anyway — <c>Max(1, floor(gap * 0))</c> is 1 — so "never shrink" looked expressible here
        /// and silently was not. Set <see cref="EnableAutoResize"/> to <c>false</c> for that.
        /// </remarks>
        public float ShrinkFactor = 0.25f;

        /// <summary>
        /// How long (in seconds) usage must stay below shrink threshold before actually shrinking
        /// This prevents thrashing from temporary usage dips
        /// </summary>
        public float ShrinkDelaySeconds = 30f;

        /// <summary>
        /// Enable automatic pool resizing based on usage patterns
        /// </summary>
        public bool EnableAutoResize = true;

        /// <summary>
        /// Log pool resize operations for debugging
        /// </summary>
        public bool LogResizeOperations = true;

        /// <summary>
        /// Default balanced configuration
        /// </summary>
        public static DynamicPoolConfig Default => new DynamicPoolConfig
        {
            InitialCapacity = 10,
            MinSize = 5,
            MaxSize = 100,
            GrowThreshold = 0.8f,
            ShrinkThreshold = 0.3f,
            GrowFactor = 0.5f,
            ShrinkFactor = 0.25f,
            ShrinkDelaySeconds = 30f,
            EnableAutoResize = true,
            LogResizeOperations = true
        };

        /// <summary>
        /// Conservative configuration (grows quickly, shrinks slowly)
        /// Good for variable usage patterns
        /// </summary>
        public static DynamicPoolConfig Conservative => new DynamicPoolConfig
        {
            InitialCapacity = 15,
            MinSize = 10,
            MaxSize = 150,
            GrowThreshold = 0.7f,
            ShrinkThreshold = 0.2f,
            GrowFactor = 0.75f,
            ShrinkFactor = 0.15f,
            ShrinkDelaySeconds = 60f,
            EnableAutoResize = true,
            LogResizeOperations = false
        };

        /// <summary>
        /// Aggressive configuration (tight memory usage, quick adaptation)
        /// Good for mobile or memory-constrained devices
        /// </summary>
        public static DynamicPoolConfig Aggressive => new DynamicPoolConfig
        {
            InitialCapacity = 5,
            MinSize = 2,
            MaxSize = 50,
            GrowThreshold = 0.9f,
            ShrinkThreshold = 0.4f,
            GrowFactor = 0.3f,
            ShrinkFactor = 0.4f,
            ShrinkDelaySeconds = 15f,
            EnableAutoResize = true,
            LogResizeOperations = false
        };

        /// <summary>
        /// Fixed size configuration (no auto-resize): a plain on-demand pool with a hard cap of
        /// <paramref name="size"/> on its free list.
        /// </summary>
        /// <remarks>
        /// P-18: this creates <b>no</b> instances. "Fixed size" describes the cap, not a
        /// pre-allocated population — pass <c>preloadCount: size</c> alongside this config if you
        /// want the traditional "allocate everything up front, never allocate again" behaviour.
        /// P-19: <paramref name="size"/> must be at least 1; <c>Fixed(0)</c> now fails
        /// <see cref="Validate"/> instead of producing an unbounded pool that can never grow.
        /// </remarks>
        public static DynamicPoolConfig Fixed(int size)
        {
            return new DynamicPoolConfig
            {
                InitialCapacity = size,
                MinSize = size,
                MaxSize = size,
                EnableAutoResize = false,
                LogResizeOperations = false
            };
        }

        /// <summary>
        /// Validate configuration values
        /// </summary>
        public bool Validate(out string error)
        {
            if (InitialCapacity < 0)
            {
                error = "InitialCapacity cannot be negative";
                return false;
            }

            if (MinSize < 0)
            {
                error = "MinSize cannot be negative";
                return false;
            }

            // P-19: MaxSize is documented as a hard limit, and this type has no "unlimited"
            // encoding. Accepting 0 here used to hand a 0 straight to IPoolFactory.CreatePool, where
            // P-7 defines <= 0 as UNLIMITED — producing a pool that could never grow (capacity
            // 0 < MaxSize 0 is false) and never bounded its free list. Reject it here, where the
            // caller can still see which of the two conventions they hit.
            if (MaxSize < 1)
            {
                error = "MaxSize must be >= 1. Note that unlike IPoolFactory.CreatePool's maxSize " +
                    "parameter, 0 does NOT mean 'unlimited' on DynamicPoolConfig — this field is a " +
                    "hard limit and there is no unlimited encoding for it.";
                return false;
            }

            if (MaxSize < MinSize)
            {
                error = "MaxSize must be >= MinSize";
                return false;
            }

            if (InitialCapacity > MaxSize)
            {
                error = "InitialCapacity must be <= MaxSize";
                return false;
            }

            if (GrowThreshold < 0f || GrowThreshold > 1f)
            {
                error = "GrowThreshold must be between 0 and 1";
                return false;
            }

            if (ShrinkThreshold < 0f || ShrinkThreshold > 1f)
            {
                error = "ShrinkThreshold must be between 0 and 1";
                return false;
            }

            if (GrowFactor <= 0f)
            {
                error = "GrowFactor must be > 0";
                return false;
            }

            // P-20: > 0, mirroring GrowFactor. A ShrinkFactor of 0 reads as "never shrink" and did
            // not behave that way — Max(1, floor(gap * 0)) still shrinks by one per interval.
            if (ShrinkFactor <= 0f || ShrinkFactor > 1f)
            {
                error = "ShrinkFactor must be > 0 and <= 1. To disable shrinking, set " +
                    "EnableAutoResize = false — a ShrinkFactor of 0 does not express that (the " +
                    "shrink-by-at-least-1 floor still applies).";
                return false;
            }

            if (ShrinkDelaySeconds < 0f)
            {
                error = "ShrinkDelaySeconds cannot be negative";
                return false;
            }

            error = null;
            return true;
        }
    }
}
