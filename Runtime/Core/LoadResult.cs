using System;

namespace AddressableManager.Core
{
    /// <summary>
    /// Result type for load operations with explicit error handling
    /// Inspired by Rust's Result<T, E> pattern
    ///
    /// Usage:
    ///   var result = await loader.LoadAssetSafe<Sprite>("UI/Icon");
    ///
    ///   if (result.IsSuccess)
    ///   {
    ///       var sprite = result.Value;
    ///       // Use sprite...
    ///   }
    ///   else
    ///   {
    ///       Debug.LogError(result.Error);
    ///   }
    ///
    /// Or use Unwrap() for exception-based handling:
    ///   var sprite = result.Unwrap(); // Throws if error
    ///
    /// Or provide fallback:
    ///   var sprite = result.UnwrapOr(defaultSprite);
    /// </summary>
    public class LoadResult<T>
    {
        private readonly T _value;
        private readonly LoadError _error;

        /// <summary>
        /// Whether the operation succeeded
        /// </summary>
        public bool IsSuccess => _error == null || _error.Code == LoadErrorCode.None;

        /// <summary>
        /// Whether the operation failed
        /// </summary>
        public bool IsFailure => !IsSuccess;

        /// <summary>
        /// The loaded value (null if failed)
        /// </summary>
        public T Value => IsSuccess ? _value : default;

        /// <summary>
        /// The error (null if succeeded)
        /// </summary>
        public LoadError Error => _error;

        /// <summary>
        /// Error code
        /// </summary>
        public LoadErrorCode ErrorCode => _error?.Code ?? LoadErrorCode.None;

        /// <summary>
        /// Error message
        /// </summary>
        public string ErrorMessage => _error?.Message ?? string.Empty;

        // Private constructors - use static factory methods
        private LoadResult(T value)
        {
            _value = value;
            _error = null;
        }

        private LoadResult(LoadError error)
        {
            _value = default;
            _error = error ?? throw new ArgumentNullException(nameof(error));
        }

        #region Factory Methods

        /// <summary>
        /// Create success result
        /// </summary>
        public static LoadResult<T> Success(T value)
        {
            return new LoadResult<T>(value);
        }

        /// <summary>
        /// Create failure result
        /// </summary>
        public static LoadResult<T> Failure(LoadError error)
        {
            return new LoadResult<T>(error);
        }

        /// <summary>
        /// Create failure result with error code and message
        /// </summary>
        public static LoadResult<T> Failure(LoadErrorCode code, string message, string hint = null, string address = null, Exception exception = null)
        {
            var error = new LoadError(code, message, hint, address, exception);
            return new LoadResult<T>(error);
        }

        #endregion

        #region Unwrap Methods

        /// <summary>
        /// Get value or throw exception if failed
        /// </summary>
        /// <exception cref="InvalidOperationException">If result is failure</exception>
        public T Unwrap()
        {
            if (IsFailure)
            {
                throw new InvalidOperationException(
                    $"Cannot unwrap failed result: {_error}\n\n" +
                    $"Consider using:\n" +
                    $"  - result.UnwrapOr(defaultValue) for fallback\n" +
                    $"  - if (result.IsSuccess) {{ ... }} for conditional handling\n" +
                    $"  - result.Match(onSuccess, onFailure) for pattern matching"
                );
            }
            return _value;
        }

        /// <summary>
        /// Get value or return default if failed
        /// </summary>
        public T UnwrapOr(T defaultValue)
        {
            return IsSuccess ? _value : defaultValue;
        }

        /// <summary>
        /// Get value or compute default if failed
        /// </summary>
        public T UnwrapOrElse(Func<T> defaultFactory)
        {
            if (defaultFactory == null)
                throw new ArgumentNullException(nameof(defaultFactory));

            return IsSuccess ? _value : defaultFactory();
        }

        /// <summary>
        /// Get value or compute default from error if failed
        /// </summary>
        public T UnwrapOrElse(Func<LoadError, T> defaultFactory)
        {
            if (defaultFactory == null)
                throw new ArgumentNullException(nameof(defaultFactory));

            return IsSuccess ? _value : defaultFactory(_error);
        }

        #endregion

        #region Pattern Matching

        /// <summary>
        /// Pattern matching - execute action based on success/failure
        /// </summary>
        public void Match(Action<T> onSuccess, Action<LoadError> onFailure)
        {
            if (onSuccess == null) throw new ArgumentNullException(nameof(onSuccess));
            if (onFailure == null) throw new ArgumentNullException(nameof(onFailure));

            if (IsSuccess)
                onSuccess(_value);
            else
                onFailure(_error);
        }

        /// <summary>
        /// Pattern matching - map to result based on success/failure
        /// </summary>
        public TResult Match<TResult>(Func<T, TResult> onSuccess, Func<LoadError, TResult> onFailure)
        {
            if (onSuccess == null) throw new ArgumentNullException(nameof(onSuccess));
            if (onFailure == null) throw new ArgumentNullException(nameof(onFailure));

            return IsSuccess ? onSuccess(_value) : onFailure(_error);
        }

        #endregion

        #region LINQ-style Operations

        /// <summary>
        /// Map/Transform the success value
        /// </summary>
        public LoadResult<TResult> Map<TResult>(Func<T, TResult> mapper)
        {
            if (mapper == null)
                throw new ArgumentNullException(nameof(mapper));

            return IsSuccess
                ? LoadResult<TResult>.Success(mapper(_value))
                : LoadResult<TResult>.Failure(_error);
        }

        /// <summary>
        /// FlatMap/Bind operation for chaining results
        /// </summary>
        public LoadResult<TResult> FlatMap<TResult>(Func<T, LoadResult<TResult>> mapper)
        {
            if (mapper == null)
                throw new ArgumentNullException(nameof(mapper));

            return IsSuccess
                ? mapper(_value)
                : LoadResult<TResult>.Failure(_error);
        }

        #endregion

        #region Implicit Conversions

        /// <summary>
        /// Implicit conversion to bool (for if statements)
        /// </summary>
        public static implicit operator bool(LoadResult<T> result)
        {
            return result?.IsSuccess ?? false;
        }

        #endregion

        public override string ToString()
        {
            return IsSuccess
                ? $"Success: {_value}"
                : $"Failure: {_error}";
        }
    }
}
