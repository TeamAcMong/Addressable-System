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
        /// Initial pool capacity
        /// </summary>
        public int InitialCapacity = 10;

        /// <summary>
        /// Minimum pool size (never shrink below this)
        /// </summary>
        public int MinSize = 5;

        /// <summary>
        /// Maximum pool size (hard limit)
        /// </summary>
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
        /// How much to shrink the pool when below threshold for extended period
        /// Range: 0.0 - 1.0 (e.g., 0.25 = shrink by 25%)
        /// </summary>
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
        /// Fixed size configuration (no auto-resize)
        /// Traditional pool behavior
        /// </summary>
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

            if (ShrinkFactor < 0f || ShrinkFactor > 1f)
            {
                error = "ShrinkFactor must be between 0 and 1";
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
