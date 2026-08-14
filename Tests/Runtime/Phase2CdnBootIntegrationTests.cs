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
    /// Proves the Phase 2 runtime layer boots against a real HTTP server — design doc §7.
    /// </summary>
    /// <remarks>
    /// WHAT MAKES THIS EVIDENCE RATHER THAN A GREEN TICK
    /// Every assertion about the network is made against the SERVER's request log, not against the
    /// client's return value. Addressables in Fast Mode reads the AssetDatabase and reports success
    /// without a single byte crossing HTTP, so "CdnManager.InitializeAsync succeeded" on its own proves
    /// nothing about the CDN path. The server saying it was asked for the catalog does.
    ///
    /// PREREQUISITES, ASSERTED RATHER THAN FIXED
    /// Two -executeMethod steps must run before this fixture, and [SetUp] fails loudly with the
    /// command if they have not:
    ///   AddressableManager.Tests.PlayModeTestSetup.EnsurePackedPlayModeBuilder
    ///   AddressableManager.Tests.CdnSettingsFixture.EnsureTestSettings
    /// A test that repairs its own preconditions cannot check them.
    /// </remarks>
    [TestFixture]
    public class Phase2CdnBootIntegrationTests
    {
        private LocalContentServer _server;
        private List<LocalContentServer.RequestEventArgs> _requestLog;

        // Addressables initialises once per process and cannot be undone, so the whole fixture
        // shares a single boot. Static so it survives between test instances.
        private static bool _booted;

        // Requests the server saw during that one boot.
        private static readonly List<LocalContentServer.RequestEventArgs> _bootRequestLog =
            new List<LocalContentServer.RequestEventArgs>();

        [SetUp]
        public void SetUp()
        {
            _server = LocalContentServerMenu.Instance;
            _requestLog = new List<LocalContentServer.RequestEventArgs>();

            // Started here rather than inside each test: the one shared boot happens inside
            // whichever test runs first, and it needs the server already up.
            if (!_server.IsRunning)
                _server.Start(8080);

            Assert.IsTrue(_server.IsRunning, "The local content server must be running on 8080");

            // Force HTTP on every run. Without this, run 2 answers from the bundle cache and the
            // server log stays empty — the test would pass on run 1 and fail on run 2, or worse,
            // pass on both while proving nothing on the second.
            Caching.ClearCache();

            var settings = Resources.Load<CdnSettings>(CdnSettings.ResourceName);
            Assert.IsNotNull(settings,
                $"No CdnSettings in Resources. Run this first:\n" +
                $"  Unity -batchmode -quit -executeMethod AddressableManager.Tests.CdnSettingsFixture.EnsureTestSettings");

            // Deliberately NOT calling CdnManager.Reset() here. Reset uninstalls the hooks but
            // cannot un-initialise Addressables, which is process-global and one-way, so resetting
            // between tests would leave every test after the first unable to boot. See
            // EnsureBootedOnce.
        }

        [TearDown]
        public void TearDown()
        {
            _server.RequestReceived -= OnRequestReceived;
            _requestLog.Clear();

            // The server stays up between tests and is stopped in OneTimeTearDown. Restarting it per
            // test races the port, and the shared boot may belong to a different test.
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            if (_server != null && _server.IsRunning)
                _server.Stop();
        }

        /// <summary>
        /// Design doc §7.1 — normal boot: initialise against the CDN and fetch the catalog over HTTP.
        /// </summary>
        [UnityTest]
        public IEnumerator Boot_InitializesAgainstLocalServer_AndFetchesCatalogOverHttp()
        {
            yield return EnsureBootedOnce();

            Assert.IsTrue(CdnManager.IsInitialized, "IsInitialized must be true after a successful boot");
            Assert.AreEqual("Local", CdnManager.CurrentEnvironmentId,
                "The default environment from the fixture must be active");
            Assert.AreEqual("http://localhost:8080", CdnManager.CurrentBaseUrl);

            // The evidence: the server was actually asked for the catalog hash. Addressables polls
            // the .hash file to decide whether its cached catalog is current, so this is the request
            // that proves the remote catalog path was exercised rather than the local one.
            // Read from the boot-time log: the boot happens once per play session and may have
            // occurred inside another test's call to EnsureBootedOnce.
            bool catalogRequested = _bootRequestLog.Any(r =>
                r.Path != null && r.Path.Contains("/catalog/") &&
                (r.Path.EndsWith(".hash") || r.Path.EndsWith(".bin") || r.Path.EndsWith(".json")));

            Assert.IsTrue(catalogRequested,
                "The server received no catalog request during boot, so nothing proves initialisation " +
                $"went over HTTP. Boot requests:\n{FormatLog(_bootRequestLog)}");
        }

        /// <summary>
        /// Design doc §7.2 — the update check reaches the server and returns a definite answer.
        /// </summary>
        [UnityTest]
        public IEnumerator CheckForUpdate_ReturnsDefiniteAnswer_WhenServerIsReachable()
        {
            _server.RequestReceived += OnRequestReceived;
            yield return EnsureBootedOnce();

            var check = AsTask(CdnManager.CheckForUpdateAsync());
            while (!check.IsCompleted) yield return null;

            var result = check.Result;
            Assert.IsTrue(result.IsSuccess, $"CheckForUpdateAsync failed: {result.Error}");

            // The distinction this asserts: online, the answer must be definite. Reporting
            // WasOfflineFallback while the server is demonstrably reachable would mean the layer
            // cannot tell "nothing new" from "could not ask" — the exact confusion CatalogUpdateInfo
            // exists to prevent.
            Assert.IsFalse(result.Value.WasOfflineFallback,
                "The server is running and was reached, so the check must be a real answer rather " +
                $"than an offline fallback. Requests:\n{FormatRequestLog()}");
        }

        /// <summary>
        /// Task 2.3 — installing the hooks after Addressables has initialised must fail loudly.
        /// </summary>
        /// <remarks>
        /// The failure this guards against is silent: hooks installed late are simply not consulted
        /// for anything already resolved, producing a build that fetches its catalog from one origin
        /// and some bundles from another. A refusal is the only outcome that surfaces it.
        /// </remarks>
        [UnityTest]
        public IEnumerator Decorator_RefusesToInstall_AfterAddressablesHasInitialized()
        {
            yield return EnsureBootedOnce();

            Assert.IsTrue(CdnRequestDecorator.HasAddressablesInitialized,
                "Addressables must report as initialised once a catalog has loaded");

            // Uninstall so the guard is reached: Install is idempotent while already installed and
            // would return success before ever consulting the initialisation state.
            CdnRequestDecorator.Uninstall();

            var settings = Resources.Load<CdnSettings>(CdnSettings.ResourceName);
            var rewriter = new HostRewriter(settings);
            var second = CdnRequestDecorator.Install(rewriter, settings.DownloadPolicy);

            Assert.IsTrue(second.IsFailure,
                "Installing the hooks after initialisation must fail; they would not be honoured " +
                "for content already resolved");
            Assert.IsTrue(second.ErrorMessage.Contains("already initialised"),
                $"The failure must name the cause. Got: {second.Error}");
        }

        /// <summary>
        /// Task 2.4 — the platform token must reproduce the folder the build published into.
        /// </summary>
        /// <remarks>
        /// Not a network test, but it belongs with them: getting this wrong produces a 404 on every
        /// request and nothing else in the suite would catch it. The build publishes under
        /// [BuildTarget] — "StandaloneWindows64" — while Unity's own
        /// PlatformMappingService.GetPlatformPathSubFolder() returns "Windows" for the same target.
        /// </remarks>
        [Test]
        public void PlatformToken_MatchesTheBuildTargetFolder_NotAddressablesPlatformName()
        {
            string token = HostRewriter.ResolvePlatformToken();

#if UNITY_EDITOR
            Assert.AreEqual(UnityEditor.EditorUserBuildSettings.activeBuildTarget.ToString(), token,
                "In the Editor the platform token must equal the active build target, which is what " +
                "the profile variable [BuildTarget] expands to when content is published.");
#endif

            string addressablesName = UnityEngine.AddressableAssets.PlatformMappingService.GetPlatformPathSubFolder();
            Assert.AreNotEqual(addressablesName, token,
                "These are expected to differ on this platform — that difference is the whole reason " +
                "ResolvePlatformToken exists. If they have become equal, Unity changed the mapping " +
                "and the comment in HostRewriter needs revisiting.");
        }

        /// <summary>
        /// Boot the CDN layer, once per play session.
        /// </summary>
        /// <remarks>
        /// Addressables initialisation is process-global and irreversible: nothing in its public API
        /// undoes it, and CdnManager.Reset only removes our own hooks. A fixture that booted per test
        /// would pass its first test and fail every other one on the decorator's "already
        /// initialised" guard — which is exactly what this suite did before it was restructured. The
        /// guard was right; the test design was wrong.
        /// </remarks>
        private IEnumerator EnsureBootedOnce()
        {
            if (_booted)
            {
                Assert.IsTrue(CdnManager.IsInitialized,
                    "This session booted earlier but the facade reports uninitialised — something " +
                    "called CdnManager.Reset(), which cannot be recovered from in the same process.");
                yield break;
            }

            _server.RequestReceived += OnBootRequestReceived;

            var task = AsTask(CdnManager.InitializeAsync());
            while (!task.IsCompleted)
                yield return null;

            _server.RequestReceived -= OnBootRequestReceived;

            var result = task.Result;
            Assert.IsTrue(result.IsSuccess, $"CdnManager.InitializeAsync failed: {result.Error}");

            _booted = true;
        }

        private static void OnBootRequestReceived(object sender, LocalContentServer.RequestEventArgs e)
        {
            _bootRequestLog.Add(e);
        }

        // ========== helpers ==========

        /// <summary>
        /// Normalise the facade's return type so these tests compile whether or not UniTask is
        /// installed.
        /// </summary>
        /// <remarks>
        /// The public API switches between Task and UniTask on UNITASK_PRESENT (repo invariant 3).
        /// UniTask is a struct that can only be awaited once and has no pollable IsCompleted, so a
        /// coroutine test cannot yield on it directly. UniTask is not installed in this project
        /// today; this bridge means adding it later does not silently break the test suite.
        /// </remarks>
#if UNITASK_PRESENT
        private static System.Threading.Tasks.Task<T> AsTask<T>(Cysharp.Threading.Tasks.UniTask<T> task) => task.AsTask();
#else
        private static System.Threading.Tasks.Task<T> AsTask<T>(System.Threading.Tasks.Task<T> task) => task;
#endif

        private void OnRequestReceived(object sender, LocalContentServer.RequestEventArgs e)
        {
            _requestLog.Add(e);
        }

        private string FormatRequestLog() => FormatLog(_requestLog);

        private static string FormatLog(List<LocalContentServer.RequestEventArgs> log)
        {
            if (log.Count == 0)
                return "  (the server received no requests at all)";

            return string.Join("\n", log.Select(r => $"  {r.Path}"));
        }
    }
}
