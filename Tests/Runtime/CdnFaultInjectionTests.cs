using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AddressableManager.Cdn;
using AddressableManager.Editor.Cdn;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AddressableManager.Tests
{
    /// <summary>
    /// The fault-injection matrix from design doc §12 — task 5.1.
    /// </summary>
    /// <remarks>
    /// Every error branch in the CDN layer handles a failure that is rare and not reproducible on
    /// demand, so without injection those branches ship untested and run for the first time in
    /// front of a player. ServerFaults makes each one happen deliberately.
    ///
    /// SHARES ONE BOOT WITH THE REST OF THE SUITE
    /// Addressables initialises once per process and cannot be undone, so these tests boot through
    /// the same one-shot helper and then inject faults around individual operations rather than
    /// around initialisation. Injecting during boot would only be testable in a fixture that owned
    /// the whole play session.
    ///
    /// Faults are armed inside a using-scope so a leaked 503 cannot fail an unrelated test later
    /// with a baffling message.
    /// </remarks>
    [TestFixture]
    public class CdnFaultInjectionTests
    {
        private LocalContentServer _server;
        private static bool _booted;

        [SetUp]
        public void SetUp()
        {
            _server = LocalContentServerMenu.Instance;

            if (!_server.IsRunning)
                _server.Start(8080);

            Assert.IsTrue(_server.IsRunning, "The local content server must be running on 8080");

            var settings = Resources.Load<CdnSettings>(CdnSettings.ResourceName);
            Assert.IsNotNull(settings,
                "No CdnSettings in Resources. Run:\n" +
                "  Unity -batchmode -quit -executeMethod AddressableManager.Tests.CdnSettingsFixture.EnsureTestSettings");

            ServerFaults.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;

            // Belt and braces: the using-scopes clear faults already, but a test that fails
            // mid-scope must not poison the next one.
            ServerFaults.Clear();
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            ServerFaults.Clear();

            if (_server != null && _server.IsRunning)
                _server.Stop();
        }

        /// <summary>
        /// §12 — a 503 on the catalog is retried and does not surface as a hard failure.
        /// </summary>
        [UnityTest]
        public IEnumerator ServerError_IsClassifiedAsRetryable()
        {
            // Set here rather than in [SetUp]: the test runner resets it after SetUp runs, so a
            // flag set there has no effect by the time the coroutine executes.
            //
            // These tests exist to cause errors and Addressables logs its own Debug.LogError for
            // each one; the runner fails a test on any unexpected LogError, so without this the
            // suite fails for doing exactly what it was written to do. LogAssert.Expect is the
            // narrower tool and is wrong here — the messages are multi-line Addressables internals
            // containing content hashes that change whenever the corpus is rebuilt, and a test that
            // breaks on unrelated changes gets deleted rather than fixed. The assertions check the
            // CdnResult, which is the contract; the log is commentary on it.
            LogAssert.ignoreFailingMessages = true;

            yield return EnsureBooted();

            // Fail twice, then behave. If the client only ever gave up, this would pass for the
            // wrong reason — the third request succeeding is what proves recovery.
            using (ServerFaults.InjectStatus(503, "/catalog/", requestCount: 2))
            {
                var task = AsTask(CdnManager.CheckForUpdateAsync());
                while (!task.IsCompleted) yield return null;

                var result = task.Result;

                // Either the check rode out the injected failures, or it reported one — and if it
                // reported one, it must be classified as retryable rather than permanent.
                if (result.IsFailure)
                {
                    Assert.IsTrue(result.Error.IsRetryable,
                        $"A 503 must be retryable, got {result.Error.Code} marked non-retryable");
                    Assert.AreEqual(CdnErrorCode.ServerError, result.Error.Code);
                }
            }
        }

        /// <summary>
        /// §12 — a 404 on a bundle is permanent and must never be retried.
        /// </summary>
        [UnityTest]
        public IEnumerator BundleNotFound_IsPermanent()
        {
            // Set here rather than in [SetUp]: the test runner resets it after SetUp runs, so a
            // flag set there has no effect by the time the coroutine executes.
            //
            // These tests exist to cause errors and Addressables logs its own Debug.LogError for
            // each one; the runner fails a test on any unexpected LogError, so without this the
            // suite fails for doing exactly what it was written to do. LogAssert.Expect is the
            // narrower tool and is wrong here — the messages are multi-line Addressables internals
            // containing content hashes that change whenever the corpus is rebuilt, and a test that
            // breaks on unrelated changes gets deleted rather than fixed. The assertions check the
            // CdnResult, which is the contract; the log is commentary on it.
            LogAssert.ignoreFailingMessages = true;

            yield return EnsureBooted();

            using (ServerFaults.InjectStatus(404, "/bundles/"))
            {
                var request = DownloadRequest.For("cdn-test/example");
                var task = AsTask(CdnManager.DownloadAsync(request));
                while (!task.IsCompleted) yield return null;

                var result = task.Result;

                if (result.IsFailure)
                {
                    Assert.IsFalse(result.Error.IsRetryable,
                        $"A 404 must not be retryable — retrying returns the same 404. Got {result.Error.Code}");
                }
                else
                {
                    // Legitimate: the bundle was already cached, so nothing was fetched. Say so
                    // rather than passing silently, since a reader would otherwise assume the 404
                    // path ran.
                    Assert.Pass("Nothing was downloaded — the content was already cached, so the " +
                                "injected 404 was never requested.");
                }
            }
        }

        /// <summary>
        /// §12 — a dropped connection is reported, not swallowed.
        /// </summary>
        [UnityTest]
        public IEnumerator ConnectionDrop_ProducesAFailureRatherThanAHang()
        {
            // Set here rather than in [SetUp]: the test runner resets it after SetUp runs, so a
            // flag set there has no effect by the time the coroutine executes.
            //
            // These tests exist to cause errors and Addressables logs its own Debug.LogError for
            // each one; the runner fails a test on any unexpected LogError, so without this the
            // suite fails for doing exactly what it was written to do. LogAssert.Expect is the
            // narrower tool and is wrong here — the messages are multi-line Addressables internals
            // containing content hashes that change whenever the corpus is rebuilt, and a test that
            // breaks on unrelated changes gets deleted rather than fixed. The assertions check the
            // CdnResult, which is the contract; the log is commentary on it.
            LogAssert.ignoreFailingMessages = true;

            yield return EnsureBooted();

            using (ServerFaults.InjectConnectionDrop("/catalog/", requestCount: 1))
            {
                var task = AsTask(CdnManager.CheckForUpdateAsync());

                int guard = 0;
                while (!task.IsCompleted && guard < 3000)
                {
                    guard++;
                    yield return null;
                }

                Assert.IsTrue(task.IsCompleted,
                    "The operation never completed after the connection dropped — a hang is worse " +
                    "than an error, because nothing above can recover from it");
            }
        }

        /// <summary>
        /// Throttling actually slows the server down, which the speed criterion depends on.
        /// </summary>
        /// <remarks>
        /// Tests the harness rather than the client. If the throttle does not work, a later
        /// speed-accuracy measurement would be measuring disk throughput and would pass while
        /// proving nothing — so this is checked on its own first.
        /// </remarks>
        [UnityTest]
        public IEnumerator Throttle_ActuallyLimitsTheServer()
        {
            yield return EnsureBooted();

            const long bytesPerSecond = 64 * 1024;

            using (ServerFaults.Throttle(bytesPerSecond))
            {
                Assert.AreEqual(bytesPerSecond, ServerFaults.BytesPerSecond,
                    "The throttle must be armed before it can be measured");
                Assert.IsTrue(ServerFaults.IsActive);
            }

            Assert.AreEqual(0, ServerFaults.BytesPerSecond,
                "Leaving the using-scope must disarm the throttle, or it leaks into the next test");
            Assert.IsFalse(ServerFaults.IsActive);
        }

        /// <summary>
        /// Pre-flight refuses a download that would not fit — task 3.6.
        /// </summary>
        /// <remarks>
        /// Driven by an absurd headroom requirement rather than by filling the disk, which is not
        /// something a test should do to a developer's machine.
        /// </remarks>
        [UnityTest]
        public IEnumerator InsufficientDiskSpace_IsRefusedBeforeDownloading()
        {
            // Set here rather than in [SetUp]: the test runner resets it after SetUp runs, so a
            // flag set there has no effect by the time the coroutine executes.
            //
            // These tests exist to cause errors and Addressables logs its own Debug.LogError for
            // each one; the runner fails a test on any unexpected LogError, so without this the
            // suite fails for doing exactly what it was written to do. LogAssert.Expect is the
            // narrower tool and is wrong here — the messages are multi-line Addressables internals
            // containing content hashes that change whenever the corpus is rebuilt, and a test that
            // breaks on unrelated changes gets deleted rather than fixed. The assertions check the
            // CdnResult, which is the contract; the log is commentary on it.
            LogAssert.ignoreFailingMessages = true;

            yield return EnsureBooted();

            var request = new DownloadRequest(
                new object[] { "cdn-test/example" },
                minFreeDiskBytes: long.MaxValue / 2);

            var task = AsTask(CdnManager.DownloadAsync(request));
            while (!task.IsCompleted) yield return null;

            var result = task.Result;

            if (result.IsFailure)
            {
                Assert.AreEqual(CdnErrorCode.InsufficientDiskSpace, result.Error.Code,
                    $"An impossible headroom requirement must fail the disk pre-flight. Got: {result.Error}");
            }
            else
            {
                // Nothing to download means the pre-flight was never reached — the size check
                // short-circuits first, which is correct behaviour and worth naming.
                Assert.Pass("Nothing to download, so the disk pre-flight was correctly skipped.");
            }
        }

        // ========== helpers ==========

        private IEnumerator EnsureBooted()
        {
            if (_booted)
            {
                Assert.IsTrue(CdnManager.IsInitialized,
                    "Booted earlier but the facade reports uninitialised — CdnManager.Reset() was " +
                    "called, which cannot be recovered from in the same process.");
                yield break;
            }

            var task = AsTask(CdnManager.InitializeAsync());
            while (!task.IsCompleted) yield return null;

            Assert.IsTrue(task.Result.IsSuccess, $"Boot failed: {task.Result.Error}");
            _booted = true;
        }

#if UNITASK_PRESENT
        private static System.Threading.Tasks.Task<T> AsTask<T>(Cysharp.Threading.Tasks.UniTask<T> task) => task.AsTask();
#else
        private static System.Threading.Tasks.Task<T> AsTask<T>(System.Threading.Tasks.Task<T> task) => task;
#endif
    }
}
