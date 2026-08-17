using System;

namespace AddressableManager.Cdn
{
    /// <summary>
    /// The outcome of a CDN operation: a value, or a <see cref="CdnError"/> explaining why there
    /// isn't one.
    /// </summary>
    /// <remarks>
    /// Deliberately the same shape as <see cref="AddressableManager.Core.LoadResult{T}"/> so the
    /// package reads consistently — a caller who has used one already knows this one.
    ///
    /// It exists because of repo invariant 4: an operation that can fail returns a result, never a
    /// sentinel. Returning 0, null or false to mean two different things is how
    /// GetDownloadSizeAsync ended up unable to distinguish "nothing to download" from "could not
    /// find out", and over a network that ambiguity is the normal case rather than the edge case.
    /// </remarks>
    /// <typeparam name="T">The value produced on success.</typeparam>
    public class CdnResult<T>
    {
        private readonly T _value;
        private readonly CdnError _error;

        private CdnResult(T value, CdnError error)
        {
            _value = value;
            _error = error;
        }

        /// <summary>True when the operation produced a value.</summary>
        public bool IsSuccess => _error == null || _error.Code == CdnErrorCode.None;

        /// <summary>True when the operation failed.</summary>
        public bool IsFailure => !IsSuccess;

        /// <summary>The value, or <c>default</c> on failure. Check <see cref="IsSuccess"/> first.</summary>
        public T Value => IsSuccess ? _value : default;

        /// <summary>The error, or null on success.</summary>
        public CdnError Error => _error;

        /// <summary>The error code, or <see cref="CdnErrorCode.None"/> on success.</summary>
        public CdnErrorCode ErrorCode => _error?.Code ?? CdnErrorCode.None;

        /// <summary>The error message, or empty on success.</summary>
        public string ErrorMessage => _error?.Message ?? string.Empty;

        /// <summary>
        /// Whether retrying could succeed. False on success and on permanent failures.
        /// </summary>
        public bool IsRetryable => _error?.IsRetryable ?? false;

        /// <summary>Was the operation cancelled by the caller.</summary>
        /// <remarks>
        /// Worth its own property: cancellation is a failure for control flow but not for the user,
        /// and must not surface as an error dialog or a telemetry event.
        /// </remarks>
        public bool IsCancelled => _error?.Code == CdnErrorCode.Cancelled;

        // ========== construction ==========

        /// <summary>A successful result carrying <paramref name="value"/>.</summary>
        public static CdnResult<T> Success(T value) => new CdnResult<T>(value, null);

        /// <summary>A failed result.</summary>
        public static CdnResult<T> Failure(CdnError error)
        {
            if (error == null)
                throw new ArgumentNullException(nameof(error), "A failed CdnResult must carry an error");

            return new CdnResult<T>(default, error);
        }

        /// <summary>A failed result, built from the parts of an error.</summary>
        public static CdnResult<T> Failure(
            CdnErrorCode code,
            string message,
            string hint = null,
            string url = null,
            int httpStatusCode = 0,
            Exception exception = null)
        {
            return new CdnResult<T>(default, new CdnError(code, message, hint, url, httpStatusCode, exception));
        }

        /// <summary>A cancelled result.</summary>
        public static CdnResult<T> Cancelled(string message = "The operation was cancelled")
        {
            return new CdnResult<T>(default, new CdnError(CdnErrorCode.Cancelled, message));
        }

        // ========== consumption ==========

        /// <summary>The value, or throw. Use only where failure is genuinely unexpected.</summary>
        /// <exception cref="InvalidOperationException">The result is a failure.</exception>
        public T Unwrap()
        {
            if (IsFailure)
                throw new InvalidOperationException($"Unwrap on a failed CdnResult: {_error}");

            return _value;
        }

        /// <summary>The value, or <paramref name="defaultValue"/> on failure.</summary>
        public T UnwrapOr(T defaultValue) => IsSuccess ? _value : defaultValue;

        /// <summary>The value, or the result of <paramref name="defaultFactory"/> on failure.</summary>
        public T UnwrapOrElse(Func<CdnError, T> defaultFactory)
        {
            if (defaultFactory == null) throw new ArgumentNullException(nameof(defaultFactory));
            return IsSuccess ? _value : defaultFactory(_error);
        }

        /// <summary>Run one branch or the other.</summary>
        public void Match(Action<T> onSuccess, Action<CdnError> onFailure)
        {
            if (onSuccess == null) throw new ArgumentNullException(nameof(onSuccess));
            if (onFailure == null) throw new ArgumentNullException(nameof(onFailure));

            if (IsSuccess) onSuccess(_value);
            else onFailure(_error);
        }

        /// <summary>Collapse both branches to one value.</summary>
        public TResult Match<TResult>(Func<T, TResult> onSuccess, Func<CdnError, TResult> onFailure)
        {
            if (onSuccess == null) throw new ArgumentNullException(nameof(onSuccess));
            if (onFailure == null) throw new ArgumentNullException(nameof(onFailure));

            return IsSuccess ? onSuccess(_value) : onFailure(_error);
        }

        /// <summary>Transform the value, carrying any failure through untouched.</summary>
        public CdnResult<TResult> Map<TResult>(Func<T, TResult> mapper)
        {
            if (mapper == null) throw new ArgumentNullException(nameof(mapper));

            return IsSuccess
                ? CdnResult<TResult>.Success(mapper(_value))
                : CdnResult<TResult>.Failure(_error);
        }

        /// <summary>Chain another fallible step, carrying any failure through untouched.</summary>
        public CdnResult<TResult> FlatMap<TResult>(Func<T, CdnResult<TResult>> mapper)
        {
            if (mapper == null) throw new ArgumentNullException(nameof(mapper));

            return IsSuccess
                ? mapper(_value)
                : CdnResult<TResult>.Failure(_error);
        }

        /// <summary>
        /// Implicit truthiness, so <c>if (result)</c> reads naturally — matching LoadResult.
        /// </summary>
        public static implicit operator bool(CdnResult<T> result) => result != null && result.IsSuccess;

        /// <inheritdoc />
        public override string ToString()
        {
            return IsSuccess
                ? $"CdnResult.Success({_value})"
                : $"CdnResult.Failure({_error})";
        }
    }
}
