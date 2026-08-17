using System;

namespace AddressableManager.Cdn
{
    /// <summary>
    /// Decides whether and when to retry a failed CDN operation — task 3.1.
    /// </summary>
    /// <remarks>
    /// No Unity dependency of any kind, so it is unit-testable in EditMode without entering play
    /// mode or standing up a server. That is deliberate: backoff maths is exactly the sort of thing
    /// that looks obviously right and is off by a factor of two.
    ///
    /// JITTER IS NOT DECORATION
    /// When a CDN edge hiccups it fails every client at once, and without jitter every one of them
    /// retries at the same instant — the thundering herd that turns a blip into an outage. The delay
    /// is therefore randomised across a window rather than being a clean doubling.
    /// </remarks>
    public class RetryPolicy
    {
        private readonly Random _random;

        /// <summary>Attempts after the first. Zero disables retrying.</summary>
        public int MaxRetries { get; }

        /// <summary>Delay before the first retry.</summary>
        public TimeSpan BaseDelay { get; }

        /// <summary>Ceiling on the delay, before jitter.</summary>
        public TimeSpan MaxDelay { get; }

        /// <summary>
        /// Fraction of the computed delay that is randomised, 0..1.
        /// </summary>
        /// <remarks>
        /// 0.5 means the actual delay lands somewhere in 50%..100% of the computed value — the
        /// "equal jitter" shape. Full randomisation (1.0) spreads clients best but can retry almost
        /// immediately, which wastes an attempt when the server needs a moment.
        /// </remarks>
        public double JitterFactor { get; }

        /// <summary>Create a policy.</summary>
        /// <param name="seed">Fixed seed for tests. Omit for time-seeded randomness.</param>
        public RetryPolicy(
            int maxRetries = 3,
            TimeSpan? baseDelay = null,
            TimeSpan? maxDelay = null,
            double jitterFactor = 0.5,
            int? seed = null)
        {
            if (maxRetries < 0)
                throw new ArgumentOutOfRangeException(nameof(maxRetries), "Retry count cannot be negative");

            if (jitterFactor < 0 || jitterFactor > 1)
                throw new ArgumentOutOfRangeException(nameof(jitterFactor), "Jitter factor must be within 0..1");

            MaxRetries = maxRetries;
            BaseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
            MaxDelay = maxDelay ?? TimeSpan.FromSeconds(30);
            JitterFactor = jitterFactor;

            _random = seed.HasValue ? new Random(seed.Value) : new Random();
        }

        /// <summary>Defaults suitable for content downloads.</summary>
        public static RetryPolicy Default => new RetryPolicy();

        /// <summary>Never retries. For callers that own their own retry loop.</summary>
        public static RetryPolicy None => new RetryPolicy(maxRetries: 0);

        /// <summary>
        /// Whether an attempt that failed with <paramref name="error"/> should be retried.
        /// </summary>
        /// <param name="error">The failure.</param>
        /// <param name="attemptsSoFar">Attempts already made, including the first. 1 after one failure.</param>
        /// <remarks>
        /// Retryability comes from the error itself (design doc §8), not from this policy, so the
        /// CLI, the GUI and the runtime all agree about what is worth retrying. The policy only
        /// decides how many times and how long to wait.
        /// </remarks>
        public bool ShouldRetry(CdnError error, int attemptsSoFar)
        {
            if (error == null) return false;
            if (attemptsSoFar > MaxRetries) return false;

            // Cancellation is not a failure to retry — the caller asked to stop.
            if (error.Code == CdnErrorCode.Cancelled) return false;

            return error.IsRetryable;
        }

        /// <summary>
        /// How long to wait before attempt number <paramref name="attemptsSoFar"/> + 1.
        /// </summary>
        /// <param name="attemptsSoFar">Attempts already made. 1 after the first failure.</param>
        public TimeSpan GetDelay(int attemptsSoFar)
        {
            if (attemptsSoFar < 1) attemptsSoFar = 1;

            // 2^(n-1) growth, computed in double to avoid overflowing the shift on a large retry
            // count before Math.Min clamps it.
            double exponential = BaseDelay.TotalMilliseconds * Math.Pow(2, attemptsSoFar - 1);
            double capped = Math.Min(exponential, MaxDelay.TotalMilliseconds);

            // Equal jitter: keep (1 - JitterFactor) of the delay fixed, randomise the rest.
            double fixedPart = capped * (1 - JitterFactor);
            double randomPart = capped * JitterFactor * _random.NextDouble();

            return TimeSpan.FromMilliseconds(fixedPart + randomPart);
        }

        /// <inheritdoc />
        public override string ToString() =>
            $"RetryPolicy(max={MaxRetries}, base={BaseDelay.TotalSeconds:F1}s, cap={MaxDelay.TotalSeconds:F0}s, jitter={JitterFactor:P0})";
    }
}
