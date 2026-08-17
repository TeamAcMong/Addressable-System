using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.TestTools;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using AddressableManager.Editor.Cdn;

namespace AddressableManager.Tests
{
    /// <summary>
    /// Phase 0 integration test: Verify remote asset fetch from local HTTP server.
    ///
    /// Criterion: "A player build with `Local` profile boots, fetches its catalog from `localhost:8080`,
    /// and loads a remote asset — with zero new runtime code written."
    ///
    /// This test proves the configuration is correct before any abstraction layers.
    /// The critical trap: if Editor resolution uses Fast Mode (AssetDatabase), Addressables never
    /// makes an HTTP request, and the test passes despite proving nothing.
    ///
    /// Solution: Explicitly set play mode to "Use Existing Build" (packed mode) and independently
    /// verify the server actually received a bundle request.
    /// </summary>
    [TestFixture]
    public class Phase0LocalServerIntegrationTests
    {
        private LocalContentServer server;
        private List<LocalContentServer.RequestEventArgs> requestLog;

        [SetUp]
        public void SetUp()
        {
            server = LocalContentServerMenu.Instance;
            requestLog = new List<LocalContentServer.RequestEventArgs>();

            // Clear the asset bundle cache to ensure HTTP is exercised on every run.
            // Unity's Caching.ClearCache() deletes locally cached bundles, but NOT the build-time
            // catalog (stored separately in Library/com.unity.addressables). This leaves us in a
            // known state: the build catalog is present for hash comparison, but bundles must be
            // re-downloaded over HTTP. This ensures both Run 1 (first run, cold cache) and Run 2+
            // (subsequent runs, cache cleared in SetUp) fetch the bundle over HTTP and pass
            // criterion 2 identically.
            //
            // Why this matters:
            // - Run 1 (cold cache): Bundle not cached → fetched over HTTP ✓
            // - Run 2 (after clear): Bundle cache cleared in SetUp → fetched over HTTP again ✓
            // - Both runs: Catalog hash fetches, compares to build catalog (still present),
            //   finds match, skips .bin download. Criterion 4b (no .bin re-fetch) passes both.
            //
            // This is the only way to make the test repeatable without becoming order-dependent.
            Caching.ClearCache();

            // The play mode must be set to packed mode BEFORE play mode is entered.
            // It is selected when play mode starts, so changing it in [SetUp] has no effect.
            //
            // BEFORE running this test, execute:
            //   Unity -batchmode -quit -executeMethod AddressableManager.Tests.PlayModeTestSetup.EnsurePackedPlayModeBuilder
            //
            // Then run:
            //   Unity -batchmode -runTests -testPlatform PlayMode
            //
            AssertPackedPlayModeIsActive();
        }

        [TearDown]
        public void TearDown()
        {
            // Unsubscribe from server events BEFORE stopping.
            // A stale event handler holding a reference to this test's requestLog can cause
            // cleanup to fail and leave port 8080 locked for the next test run.
            server.RequestReceived -= OnRequestReceived;

            // Stop the server to release port 8080
            if (server.IsRunning)
            {
                server.Stop();
            }

            // Clear the request log
            requestLog.Clear();
        }

        [UnityTest]
        public IEnumerator Phase0_LocalServerFetchesRemoteAsset()
        {
            // Start the server on port 8080 (the port baked into Local profile URLs)
            server.Start(8080);
            Assert.IsTrue(server.IsRunning, "Server must be running");
            Assert.AreEqual(8080, server.ActivePort, "Server must be on port 8080");

            // Subscribe to requests to verify HTTP is actually used
            server.RequestReceived += OnRequestReceived;

            try
            {
                // Load the remote asset through Addressables
                // This asset is in the "Remote Test" group and its catalog URL points to localhost:8080
                // Using TextAsset ensures the type is always available at runtime (not an Editor-only type)
                var asyncOp = Addressables.LoadAssetAsync<TextAsset>("cdn-test/example");
                yield return asyncOp;

                // Criterion 1: Asset must load successfully
                Assert.AreEqual(AsyncOperationStatus.Succeeded, asyncOp.Status,
                    $"Asset load must succeed. Status: {asyncOp.Status}. Exception: {asyncOp.OperationException?.Message}. " +
                    $"DebugName: {asyncOp.DebugName}");
                Assert.IsNotNull(asyncOp.Result, "Loaded asset must not be null");
                Assert.IsNotNull(asyncOp.Result.text, "TextAsset.text must not be null");

                // Criterion 2: HTTP was actually used - bundle must have been fetched over network
                // This proves Fast Mode (AssetDatabase) was NOT used.
                var bundleRequest = requestLog.FirstOrDefault(r =>
                    r.Path.EndsWith(".bundle") && (r.StatusCode == 200 || r.StatusCode == 206));
                Assert.IsNotNull(bundleRequest,
                    $"Bundle must be fetched over HTTP. Actual requests:\n{FormatRequestLog(requestLog)}");

                // Criterion 3: Assert cache headers match infrastructure policy (§3)
                AssertCacheHeadersForBundle(bundleRequest);

                // Criterion 4: Catalog hash must be checked (design §7.2 warm-boot optimization)
                // This always happens on every boot to check if the catalog has changed.
                var catalogHashRequest = requestLog.FirstOrDefault(r =>
                    r.Path.Contains("catalog") && r.Path.EndsWith(".hash"));
                Assert.IsNotNull(catalogHashRequest,
                    $"Catalog hash must be fetched (warm-boot check). Requests:\n{FormatRequestLog(requestLog)}");
                AssertCacheHeadersForCatalog(catalogHashRequest, "catalog.hash");

                // Criterion 4b: On a warm boot with unchanged content, the catalog body should NOT be re-fetched.
                // This is the bandwidth optimization that the hash-check design enables (§7.2).
                // If a future change makes Addressables re-download the body unnecessarily, this will catch it.
                var catalogBinRequest = requestLog.FirstOrDefault(r =>
                    r.Path.Contains("catalog") && r.Path.EndsWith(".bin"));
                Assert.IsNull(catalogBinRequest,
                    $"Catalog body should NOT be re-fetched on a warm boot with unchanged content. " +
                    $"This proves the hash-check bandwidth optimization works correctly. " +
                    $"Requests:\n{FormatRequestLog(requestLog)}");

                // Criterion 5: All requests must match the infrastructure cache policy matrix
                foreach (var request in requestLog)
                {
                    Assert.IsTrue(request.PathMatchesInfraPolicy,
                        $"Request path '{request.Path}' must match infrastructure policy. " +
                        $"A path falling through to no-store indicates a potential cache rule mismatch.");
                }

                // Criterion 6: Asset content must match exactly (right bytes over HTTP, not cached from disk)
                // This proves the right bytes came over HTTP and were deserialized correctly.
                var expectedContent = GetExpectedTestContent();
                Assert.AreEqual(expectedContent, asyncOp.Result.text,
                    $"TextAsset content must match expected value. This proves the right bytes came over HTTP.");

                // Release the asset
                Addressables.Release(asyncOp);
            }
            finally
            {
                // Ensure we unsubscribe even if an assertion fails
                server.RequestReceived -= OnRequestReceived;
            }
        }

        private void OnRequestReceived(object sender, LocalContentServer.RequestEventArgs e)
        {
            requestLog.Add(e);
            Debug.Log($"[Phase0Test] Request: {e.Method} {e.Path} → {e.StatusCode} " +
                $"({e.CacheControl}). PathMatches={e.PathMatchesInfraPolicy}");
        }

        private void AssertCacheHeadersForBundle(LocalContentServer.RequestEventArgs request)
        {
            // Per infrastructure policy §3: Bundle cache-control must be:
            // public, max-age=31536000 (1 year), immutable
            Assert.IsNotNull(request, "Bundle request must exist");
            var cc = request.CacheControl;

            Assert.IsTrue(cc.Contains("public"),
                $"Bundle must have 'public' in cache-control. Got: '{cc}'");
            Assert.IsTrue(cc.Contains("max-age=31536000"),
                $"Bundle must have 'max-age=31536000' in cache-control. Got: '{cc}'");
            Assert.IsTrue(cc.Contains("immutable"),
                $"Bundle must have 'immutable' in cache-control. Got: '{cc}'");
        }

        private void AssertCacheHeadersForCatalog(LocalContentServer.RequestEventArgs request, string filename)
        {
            // Per infrastructure policy §3: Catalog cache-control must be:
            // public, max-age=0, must-revalidate
            Assert.IsNotNull(request,
                $"{filename} must be fetched over HTTP. Requests:\n{FormatRequestLog(requestLog)}");

            var cc = request.CacheControl;
            Assert.IsTrue(cc.Contains("must-revalidate"),
                $"Catalog {filename} must have 'must-revalidate' in cache-control. Got: '{cc}'");
            Assert.IsTrue(cc.Contains("max-age=0"),
                $"Catalog {filename} must have 'max-age=0' in cache-control. Got: '{cc}'");
        }

        private void AssertPackedPlayModeIsActive()
        {
            // Verify the active play-mode builder is the packed mode builder (BuildScriptPackedPlayMode).
            // This assertion catches silent test failures where the test runs in Fast Mode (AssetDatabase)
            // and never makes HTTP requests, yet reports success.
            //
            // If this fails, the play mode was not set before entering play mode.
            // The play-mode builder is selected when play mode is entered, not changed by [SetUp].
            // Use: Unity -batchmode -quit -executeMethod AddressableManager.Tests.PlayModeTestSetup.EnsurePackedPlayModeBuilder

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Assert.Fail("[Phase0Test] AddressableAssetSettings not found. Project may not be initialized.");
                return;
            }

            int activeIndex = ProjectConfigData.ActivePlayModeIndex;
            var dataBuilders = settings.DataBuilders;

            if (activeIndex < 0 || activeIndex >= dataBuilders.Count)
            {
                Assert.Fail($"[Phase0Test] Active play mode index {activeIndex} is out of range. " +
                    $"Available builders: {string.Join(", ", dataBuilders.Cast<UnityEngine.Object>().Select(b => b?.GetType().Name ?? "null"))}");
                return;
            }

            var activeBuilder = dataBuilders[activeIndex];
            var activeBuilderTypeName = activeBuilder?.GetType().Name ?? "null";

            Assert.AreEqual("BuildScriptPackedPlayMode", activeBuilderTypeName,
                $"[Phase0Test] Play mode builder must be 'BuildScriptPackedPlayMode' (Use Existing Build), " +
                $"but it is '{activeBuilderTypeName}'. " +
                $"This test must run with the packed play-mode builder to verify HTTP behavior. " +
                $"Execute before running tests: " +
                $"Unity -batchmode -quit -executeMethod AddressableManager.Tests.PlayModeTestSetup.EnsurePackedPlayModeBuilder");
        }

        private string FormatRequestLog(List<LocalContentServer.RequestEventArgs> log)
        {
            if (log.Count == 0) return "(no requests made)";
            return string.Join("\n", log.Select(r => $"  {r.Method} {r.Path} → {r.StatusCode} ({r.CacheControl})"));
        }

        private string GetExpectedTestContent()
        {
            // Must stay byte-identical to CdnTestContentCLI.TestContentString, which is what gets
            // written to Assets/Examples/CdnTest/test-content.txt and packed into the remote bundle.
            // Kept as a literal rather than referencing the Editor constant so the assertion still
            // means something if the two ever drift apart.
            return "CDN test content - Phase 0 task 0.8 remote asset verification.";
        }
    }
}
