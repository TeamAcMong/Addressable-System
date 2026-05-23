namespace AddressableManager.Core
{
    /// <summary>
    /// Error codes for asset loading failures
    /// </summary>
    public enum LoadErrorCode
    {
        /// <summary>
        /// No error - operation succeeded
        /// </summary>
        None = 0,

        /// <summary>
        /// Asset address is null, empty, or invalid
        /// </summary>
        InvalidAddress = 1,

        /// <summary>
        /// Asset not found at specified address
        /// </summary>
        AssetNotFound = 2,

        /// <summary>
        /// AssetReference is null or invalid
        /// </summary>
        InvalidAssetReference = 3,

        /// <summary>
        /// Label is null, empty, or no assets found with label
        /// </summary>
        InvalidLabel = 4,

        /// <summary>
        /// Addressables operation failed (check exception for details)
        /// </summary>
        OperationFailed = 5,

        /// <summary>
        /// Loader is disposed
        /// </summary>
        LoaderDisposed = 6,

        /// <summary>
        /// Thread safety violation (called from wrong thread)
        /// </summary>
        ThreadSafetyViolation = 7,

        /// <summary>
        /// Type mismatch (asset exists but wrong type)
        /// </summary>
        TypeMismatch = 8,

        /// <summary>
        /// Network error during download
        /// </summary>
        NetworkError = 9,

        /// <summary>
        /// Unknown error
        /// </summary>
        Unknown = 999
    }

    /// <summary>
    /// Detailed error information for asset loading failures
    /// </summary>
    public class LoadError
    {
        /// <summary>
        /// Error code
        /// </summary>
        public LoadErrorCode Code { get; }

        /// <summary>
        /// Human-readable error message
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// Detailed troubleshooting hint
        /// </summary>
        public string Hint { get; }

        /// <summary>
        /// The address that failed to load (if applicable)
        /// </summary>
        public string Address { get; }

        /// <summary>
        /// Inner exception (if any)
        /// </summary>
        public System.Exception Exception { get; }

        public LoadError(LoadErrorCode code, string message, string hint = null, string address = null, System.Exception exception = null)
        {
            Code = code;
            Message = message;
            Hint = hint ?? GetDefaultHint(code);
            Address = address;
            Exception = exception;
        }

        private static string GetDefaultHint(LoadErrorCode code)
        {
            return code switch
            {
                LoadErrorCode.InvalidAddress =>
                    "1. Check address spelling\n" +
                    "2. Verify asset is marked as Addressable\n" +
                    "3. Check Addressables Groups window",

                LoadErrorCode.AssetNotFound =>
                    "1. Asset might not be built in Addressables\n" +
                    "2. Check address matches exactly\n" +
                    "3. Build Addressables content (Build → New Build → Default Build Script)",

                LoadErrorCode.InvalidAssetReference =>
                    "1. Ensure AssetReference is assigned in Inspector\n" +
                    "2. Check the asset still exists\n" +
                    "3. Try reassigning the reference",

                LoadErrorCode.InvalidLabel =>
                    "1. Verify label exists in Addressables Groups\n" +
                    "2. Check at least one asset has this label\n" +
                    "3. Labels are case-sensitive",

                LoadErrorCode.OperationFailed =>
                    "1. Check Console for Addressables errors\n" +
                    "2. Verify Addressables content is built\n" +
                    "3. Check exception details for more info",

                LoadErrorCode.LoaderDisposed =>
                    "1. Don't use loader after Dispose()\n" +
                    "2. Check scope lifecycle\n" +
                    "3. Create new loader if needed",

                LoadErrorCode.ThreadSafetyViolation =>
                    "1. Use ThreadSafeAssetLoader for background loading\n" +
                    "2. Or dispatch to main thread with UnityMainThreadDispatcher\n" +
                    "3. AssetLoader must be called from main thread only",

                LoadErrorCode.TypeMismatch =>
                    "1. Verify you're loading with correct type\n" +
                    "2. Check asset type in Project window\n" +
                    "3. Asset might need to be reimported",

                LoadErrorCode.NetworkError =>
                    "1. Check internet connection\n" +
                    "2. Verify remote catalog is accessible\n" +
                    "3. Check Addressables remote settings",

                _ => "Check logs for more details"
            };
        }

        public override string ToString()
        {
            var result = $"[{Code}] {Message}";
            if (!string.IsNullOrEmpty(Address))
            {
                result += $"\nAddress: {Address}";
            }
            if (!string.IsNullOrEmpty(Hint))
            {
                result += $"\n\nTroubleshooting:\n{Hint}";
            }
            if (Exception != null)
            {
                result += $"\n\nException: {Exception.Message}";
            }
            return result;
        }
    }
}
