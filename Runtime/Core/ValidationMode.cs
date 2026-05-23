using System;

namespace AddressableManager.Core
{
    /// <summary>
    /// Validation mode for runtime checks
    /// </summary>
    [Flags]
    public enum ValidationMode
    {
        /// <summary>
        /// No validation (production mode)
        /// </summary>
        None = 0,

        /// <summary>
        /// Validate asset addresses exist before loading
        /// </summary>
        ValidateAddresses = 1 << 0,

        /// <summary>
        /// Check for potential memory leaks (assets with high ref counts)
        /// </summary>
        CheckMemoryLeaks = 1 << 1,

        /// <summary>
        /// Validate AssetReference objects before loading
        /// </summary>
        ValidateAssetReferences = 1 << 2,

        /// <summary>
        /// Check for duplicate loads (same asset loaded multiple times)
        /// </summary>
        CheckDuplicateLoads = 1 << 3,

        /// <summary>
        /// Validate pool configurations
        /// </summary>
        ValidatePoolConfigs = 1 << 4,

        /// <summary>
        /// Check thread safety (warn if called from wrong thread)
        /// </summary>
        CheckThreadSafety = 1 << 5,

        /// <summary>
        /// Track and warn about unbalanced Retain/Release calls
        /// </summary>
        CheckReferenceBalance = 1 << 6,

        /// <summary>
        /// Validate cache sizes and memory limits
        /// </summary>
        ValidateCacheLimits = 1 << 7,

        /// <summary>
        /// Log all validation checks (verbose)
        /// </summary>
        VerboseLogging = 1 << 8,

        /// <summary>
        /// Development mode - all basic checks except verbose logging
        /// </summary>
        Development = ValidateAddresses | CheckMemoryLeaks | ValidateAssetReferences |
                      CheckDuplicateLoads | CheckThreadSafety | CheckReferenceBalance,

        /// <summary>
        /// Full validation - all checks including verbose logging
        /// </summary>
        Full = Development | ValidatePoolConfigs | ValidateCacheLimits | VerboseLogging,

        /// <summary>
        /// Production mode - only critical checks, no performance impact
        /// </summary>
        Production = CheckMemoryLeaks | CheckThreadSafety
    }
}
