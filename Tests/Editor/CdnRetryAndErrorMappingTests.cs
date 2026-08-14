using System;
using AddressableManager.Cdn;
using NUnit.Framework;
using UnityEngine.Networking;
using UnityEngine.ResourceManagement.Exceptions;
// UnityWebRequestResult sits in ResourceManagement.Util, not alongside the exception that carries
// it — the same near-miss namespace split as IResourceLocator vs IResourceLocation.
using UnityEngine.ResourceManagement.Util;

namespace AddressableManager.Tests
{
    /// <summary>
    /// Unit tests for the two Phase 3 pieces that need no network — tasks 3.1 and 3.7.
    /// </summary>
    /// <remarks>
    /// RetryPolicy has no Unity dependency by design, and the error mapper only needs an exception
    /// to classify, so both are testable in EditMode without a server or play mode. Backoff maths
    /// and status-code branching are exactly the sort of thing that reads correctly and is wrong.
    /// </remarks>
    [TestFixture]
    public class CdnRetryAndErrorMappingTests
    {
        // ========== 3.1 RetryPolicy ==========

        [Test]
        public void Retry_IsRefused_ForErrorsThatCannotSucceedOnRetry()
        {
            var policy = new RetryPolicy(maxRetries: 5, seed: 1);

            // These mean the content on the CDN is wrong. Asking again returns the same 404.
            foreach (var code in new[]
                     {
                         CdnErrorCode.CatalogNotFound,
                         CdnErrorCode.BundleNotFound,
                         CdnErrorCode.CatalogParseFailed,
                         CdnErrorCode.CatalogVersionIncompatible
                     })
            {
                Assert.IsFalse(policy.ShouldRetry(new CdnError(code, "x"), attemptsSoFar: 1),
                    $"{code} must not be retried — retrying cannot change the answer");
            }
        }

        [Test]
        public void Retry_IsAllowed_ForTransientErrors_UntilTheLimit()
        {
            var policy = new RetryPolicy(maxRetries: 2, seed: 1);
            var error = new CdnError(CdnErrorCode.ServerError, "503");

            Assert.IsTrue(policy.ShouldRetry(error, attemptsSoFar: 1));
            Assert.IsTrue(policy.ShouldRetry(error, attemptsSoFar: 2));
            Assert.IsFalse(policy.ShouldRetry(error, attemptsSoFar: 3),
                "The retry budget must be respected, or a 5xx becomes an infinite loop");
        }

        [Test]
        public void Retry_IsRefused_ForCancellation()
        {
            var policy = new RetryPolicy(maxRetries: 5, seed: 1);

            Assert.IsFalse(policy.ShouldRetry(new CdnError(CdnErrorCode.Cancelled, "stopped"), 1),
                "Cancellation is the caller asking to stop, not a failure to retry past");
        }

        [Test]
        public void Backoff_Grows_AndIsCapped()
        {
            // Jitter off, so the growth curve itself is under test rather than the randomness.
            var policy = new RetryPolicy(
                maxRetries: 10,
                baseDelay: TimeSpan.FromSeconds(1),
                maxDelay: TimeSpan.FromSeconds(8),
                jitterFactor: 0,
                seed: 1);

            Assert.AreEqual(1.0, policy.GetDelay(1).TotalSeconds, 0.001);
            Assert.AreEqual(2.0, policy.GetDelay(2).TotalSeconds, 0.001);
            Assert.AreEqual(4.0, policy.GetDelay(3).TotalSeconds, 0.001);
            Assert.AreEqual(8.0, policy.GetDelay(4).TotalSeconds, 0.001);

            // Capped, and still capped far out — the exponent must not overflow into something odd.
            Assert.AreEqual(8.0, policy.GetDelay(5).TotalSeconds, 0.001);
            Assert.AreEqual(8.0, policy.GetDelay(40).TotalSeconds, 0.001,
                "A large attempt count must stay clamped rather than overflowing");
        }

        [Test]
        public void Jitter_SpreadsDelays_WithinTheExpectedWindow()
        {
            var policy = new RetryPolicy(
                baseDelay: TimeSpan.FromSeconds(4),
                maxDelay: TimeSpan.FromSeconds(60),
                jitterFactor: 0.5,
                seed: 12345);

            bool sawDifference = false;
            double first = policy.GetDelay(1).TotalSeconds;

            for (int i = 0; i < 50; i++)
            {
                double delay = policy.GetDelay(1).TotalSeconds;

                // Equal jitter: 50%..100% of the computed 4s.
                Assert.GreaterOrEqual(delay, 2.0 - 0.001, "Jitter must not drop below half the delay");
                Assert.LessOrEqual(delay, 4.0 + 0.001, "Jitter must not exceed the computed delay");

                if (Math.Abs(delay - first) > 0.0001) sawDifference = true;
            }

            // The whole point of jitter: without variation every client retries on the same tick.
            Assert.IsTrue(sawDifference, "Jitter produced identical delays, which defeats its purpose");
        }

        [Test]
        public void RetryPolicy_RejectsNonsenseConfiguration()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(maxRetries: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(jitterFactor: 1.5));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(jitterFactor: -0.1));
        }

        // ========== 3.7 error mapping ==========

        [Test]
        public void Map_ReturnsCancelled_ForOperationCanceled()
        {
            var error = CdnErrorMapper.Map(new OperationCanceledException());

            Assert.AreEqual(CdnErrorCode.Cancelled, error.Code);
            Assert.IsFalse(error.IsRetryable, "Cancellation is not something to retry");
        }

        [Test]
        public void Map_UsesReachability_WhenThereIsNoHttpResponseToRead()
        {
            var exception = new Exception("something went wrong");

            var offline = CdnErrorMapper.Map(exception, "http://cdn.example.com", isReachable: false);
            Assert.AreEqual(CdnErrorCode.Offline, offline.Code,
                "With no response and no network, the honest answer is Offline");
            Assert.IsTrue(offline.IsRetryable);

            var online = CdnErrorMapper.Map(exception, "http://cdn.example.com", isReachable: true);
            Assert.AreEqual(CdnErrorCode.Unknown, online.Code,
                "Reachable with no response to classify must stay Unknown rather than guess a code");
        }

        [Test]
        public void Map_DistinguishesCatalogFromBundle_OnA404()
        {
            var catalog = MapWithStatus(404, "http://cdn.example.com/StandaloneWindows64/catalog/1.0.0/catalog_1.0.0.hash");
            Assert.AreEqual(CdnErrorCode.CatalogNotFound, catalog.Code);

            var bundle = MapWithStatus(404, "http://cdn.example.com/StandaloneWindows64/bundles/thing_abc.bundle");
            Assert.AreEqual(CdnErrorCode.BundleNotFound, bundle.Code);

            // Both mean a broken deploy, and neither is worth retrying.
            Assert.IsFalse(catalog.IsRetryable);
            Assert.IsFalse(bundle.IsRetryable);
        }

        [Test]
        public void Map_ClassifiesTheStatusCodeFamilies()
        {
            Assert.AreEqual(CdnErrorCode.Unauthorized, MapWithStatus(401, "http://x/y").Code);
            Assert.AreEqual(CdnErrorCode.Unauthorized, MapWithStatus(403, "http://x/y").Code);
            Assert.AreEqual(CdnErrorCode.ServerError, MapWithStatus(429, "http://x/y").Code);
            Assert.AreEqual(CdnErrorCode.ServerError, MapWithStatus(500, "http://x/y").Code);
            Assert.AreEqual(CdnErrorCode.ServerError, MapWithStatus(503, "http://x/y").Code);

            // 5xx is transient; 4xx other than the known ones is not.
            Assert.IsTrue(MapWithStatus(503, "http://x/y").IsRetryable);
            Assert.IsFalse(MapWithStatus(418, "http://x/y").IsRetryable);
        }

        [Test]
        public void Map_TreatsNoResponseAsTimeout_NotAsSuccess()
        {
            var error = MapWithStatus(0, "http://x/y");

            Assert.AreEqual(CdnErrorCode.Timeout, error.Code,
                "Status 0 means no response ever arrived — it must never read as success");
            Assert.IsTrue(error.IsRetryable);
            Assert.AreEqual(0, error.HttpStatusCode);
        }

        [Test]
        public void Map_FindsTheRemoteExceptionInsideAWrapper()
        {
            // Addressables wraps provider failures, so the useful exception is rarely outermost.
            var inner = BuildRemoteException(404, "http://cdn.example.com/bundles/a.bundle");
            var wrapped = new Exception("operation failed", new Exception("inner", inner));

            var error = CdnErrorMapper.Map(wrapped);

            Assert.AreEqual(CdnErrorCode.BundleNotFound, error.Code,
                "The mapper must walk the InnerException chain, not just inspect the outermost type");
            Assert.AreEqual(404, error.HttpStatusCode);
        }

        // ========== helpers ==========

        private static CdnError MapWithStatus(long status, string url) =>
            CdnErrorMapper.Map(BuildRemoteException(status, url));

        /// <summary>
        /// Build a RemoteProviderException carrying a real UnityWebRequestResult.
        /// </summary>
        /// <remarks>
        /// UnityWebRequestResult's only constructor takes a UnityWebRequest, and a request that was
        /// never sent reports responseCode 0 — so the status cannot be set that way. The fields are
        /// get-only, so this writes them through reflection. Ugly, and confined to the test: the
        /// alternative is a wrapper interface in production code existing purely to be mocked.
        /// </remarks>
        private static RemoteProviderException BuildRemoteException(long status, string url)
        {
            var request = UnityWebRequest.Get(url);
            var result = new UnityWebRequestResult(request);
            request.Dispose();

            SetBackingField(result, nameof(UnityWebRequestResult.ResponseCode), status);
            SetBackingField(result, nameof(UnityWebRequestResult.Url), url);

            return new RemoteProviderException($"HTTP {status}", null, result);
        }

        private static void SetBackingField(object target, string propertyName, object value)
        {
            var field = target.GetType().GetField(
                $"<{propertyName}>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            Assert.IsNotNull(field,
                $"No backing field for {propertyName} on {target.GetType().Name}. Unity changed the " +
                "property to something other than an auto-property, and this helper needs updating.");

            field.SetValue(target, value);
        }
    }
}
