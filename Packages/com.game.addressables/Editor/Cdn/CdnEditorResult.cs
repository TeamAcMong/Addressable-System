using System;

namespace AddressableManager.Editor.Cdn
{
    /// <summary>
    /// Result type for Editor-side CDN build operations with explicit error handling.
    /// Implements Invariant 4: operations that may fail return CdnEditorResult, not null/bool/sentinel.
    ///
    /// Named `CdnEditorResult` to avoid confusion with the runtime `CdnResult` (different namespace,
    /// different purpose, separate Phase 2 deliverable at Runtime/Cdn/Core/CdnResult.cs).
    /// </summary>
    /// <typeparam name="T">The success value type</typeparam>
    /// <remarks>
    /// Usage:
    /// <code>
    /// var result = ContentStateManager.ResolvePath();
    /// if (result.IsSuccess)
    /// {
    ///     string path = result.Value;
    ///     // use path...
    /// }
    /// else
    /// {
    ///     Debug.LogError(result.ErrorMessage);
    /// }
    /// </code>
    /// </remarks>
    public class CdnEditorResult<T>
    {
        private readonly T _value;
        private readonly string _errorMessage;
        private readonly bool _isSuccess;

        /// <summary>
        /// Whether the operation succeeded
        /// </summary>
        public bool IsSuccess => _isSuccess;

        /// <summary>
        /// Whether the operation failed
        /// </summary>
        public bool IsFailure => !_isSuccess;

        /// <summary>
        /// The result value (default(T) if failed)
        /// </summary>
        public T Value => _isSuccess ? _value : default;

        /// <summary>
        /// The error message (empty string if succeeded)
        /// </summary>
        public string ErrorMessage => _errorMessage ?? string.Empty;

        private CdnEditorResult(T value, bool isSuccess, string errorMessage = null)
        {
            _value = value;
            _isSuccess = isSuccess;
            _errorMessage = errorMessage;
        }

        /// <summary>
        /// Create a success result
        /// </summary>
        public static CdnEditorResult<T> Success(T value)
        {
            return new CdnEditorResult<T>(value, true);
        }

        /// <summary>
        /// Create a failure result
        /// </summary>
        public static CdnEditorResult<T> Failure(string errorMessage)
        {
            if (string.IsNullOrEmpty(errorMessage))
                errorMessage = "Operation failed (no details provided)";

            return new CdnEditorResult<T>(default, false, errorMessage);
        }

        public override string ToString()
        {
            return _isSuccess
                ? $"Success: {_value}"
                : $"Failure: {_errorMessage}";
        }
    }
}
