using UnityEngine;

namespace AddressableManager.Configs
{
    /// <summary>
    /// Debug and testing settings for Addressable Manager
    /// </summary>
    [CreateAssetMenu(fileName = "DebugSettings", menuName = "Addressable Manager/Debug Settings", order = 3)]
    public class DebugSettings : ScriptableObject
    {
        public enum LogLevel
        {
            None,
            ErrorsOnly,
            WarningsAndErrors,
            All
        }

        [Header("Logging")]
        [Tooltip("What level of logs to show")]
        public LogLevel logLevel = LogLevel.WarningsAndErrors;

        [Tooltip("Log to file in addition to console")]
        public bool logToFile = false;

        [Tooltip("Log file path (relative to persistent data path)")]
        public string logFilePath = "addressable_manager.log";

        [Header("Profiling")]
        [Tooltip("Enable detailed performance profiling")]
        public bool enableProfiling = true;

        [Tooltip("Show profiler overlay in-game (Editor only)")]
        public bool showProfilerOverlay = false;

        [Tooltip("Record performance metrics")]
        public bool recordMetrics = true;

        [Header("Simulation (Editor Only)")]
        [Tooltip("Simulate slow asset loading")]
        public bool simulateSlowLoading = false;

        [Tooltip("Artificial delay in milliseconds")]
        [Range(0, 5000)]
        public float simulatedDelayMs = 500f;

        [Tooltip("Simulate random load failures")]
        [Range(0, 100)]
        public float simulateFailureRate = 0f; // 0-100%

        [Tooltip("Simulate network conditions")]
        public bool simulateNetworkConditions = false;

        public enum NetworkType
        {
            None,
            WiFi,
            Mobile4G,
            Mobile3G,
            Slow
        }

        [Tooltip("Simulated network type")]
        public NetworkType networkSimulation = NetworkType.None;

        [Header("Validation")]
        [Tooltip("Validate asset references on load")]
        public bool validateReferences = true;

        [Tooltip("Check for memory leaks")]
        public bool detectMemoryLeaks = true;

        [Tooltip("Memory leak detection threshold (minutes)")]
        [Range(1, 60)]
        public int leakDetectionMinutes = 5;

        [Header("Warnings")]
        [Tooltip("Warn when reference count is unusually high")]
        public bool warnOnHighRefCount = true;

        [Tooltip("High reference count threshold")]
        [Range(5, 100)]
        public int highRefCountThreshold = 10;

        [Tooltip("Warn when scope memory usage is high")]
        public bool warnOnHighMemory = true;

        [Tooltip("High memory threshold (MB)")]
        [Range(10, 1000)]
        public float highMemoryThresholdMB = 100f;

        /// <summary>
        /// Should log this type of message?
        /// </summary>
        public bool ShouldLog(LogType type)
        {
            switch (logLevel)
            {
                case LogLevel.None:
                    return false;

                case LogLevel.ErrorsOnly:
                    return type == LogType.Error || type == LogType.Exception;

                case LogLevel.WarningsAndErrors:
                    return type == LogType.Error || type == LogType.Exception || type == LogType.Warning;

                case LogLevel.All:
                    return true;

                default:
                    return true;
            }
        }

        /// <summary>
        /// Get simulated delay based on network type
        /// </summary>
        public float GetNetworkDelay()
        {
            if (!simulateNetworkConditions) return 0f;

            switch (networkSimulation)
            {
                case NetworkType.WiFi:
                    return Random.Range(10f, 50f);

                case NetworkType.Mobile4G:
                    return Random.Range(50f, 150f);

                case NetworkType.Mobile3G:
                    return Random.Range(200f, 500f);

                case NetworkType.Slow:
                    return Random.Range(1000f, 3000f);

                default:
                    return 0f;
            }
        }

        /// <summary>
        /// Should simulate failure for this load?
        /// </summary>
        public bool ShouldSimulateFailure()
        {
            if (simulateFailureRate <= 0f) return false;

            return Random.Range(0f, 100f) < simulateFailureRate;
        }

        #region Singleton Access

        private static DebugSettings _instance;

        public static DebugSettings Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = Resources.Load<DebugSettings>("AddressableManager/DebugSettings");

                    if (_instance == null)
                    {
                        Debug.LogWarning("[DebugSettings] No DebugSettings found in Resources. Using defaults.");
                        _instance = CreateInstance<DebugSettings>();
                    }
                }

                return _instance;
            }
        }

        #endregion
    }
}
