using System;
using System.Collections.Generic;
using System.Linq;
using AddressableManager.Core;
using AddressableManager.Loaders;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.TestTools;
using static AddressableManager.Tests.Editor.LoaderCacheProbe;

namespace AddressableManager.Tests.Editor
{
    /// <summary>
    /// C-9 (a pin on a key that is not cached is observable) and C-13 (an eviction pass chases a
    /// number it can reach, and says so when it cannot), on both surviving tiering implementations:
    /// <see cref="TieredCache{T}"/> and <see cref="AssetLoader"/>'s built-in tiering.
    /// </summary>
    /// <remarks>
    /// WHY THIS FIXTURE EXISTS AT ALL. Before it, nothing in Tests/ touched either cache — grep for
    /// TieredCache turned up CHANGELOG, README, AdvancedAPI and the caches themselves. Refcount
    /// semantics that sharp with zero coverage is how C-1 shipped.
    ///
    /// DETERMINISM. Eviction is gated on <c>Tier == Cold || score &lt; EvictionScoreThreshold</c>,
    /// and the score is a function of <c>Time.realtimeSinceStartup</c> — a freshly inserted entry
    /// scores ~69.3 and every shipped threshold is 1.0/3.0/0.5, so "wait for it to cool" is a
    /// wall-clock race no EditMode test should be running. Every test below drives the other arm of
    /// the same gate instead: entries are marked Cold explicitly through the internal
    /// <c>GetEntriesByTier</c> seam. Nothing here sleeps, and nothing here depends on how long the
    /// previous assertion took.
    ///
    /// FAKE HANDLES. <see cref="TieredCache{T}"/> takes the public <see cref="IAssetHandle{T}"/>, so
    /// the doubles below are exactly the "foreign implementation" the production code already
    /// documents handling (<c>TryRetain</c>'s two-step fallback, <c>ForceReleaseAll</c>'s
    /// <c>is IOwnedHandle</c> test). That keeps these tests free of Addressables content, which is
    /// what makes them EditMode tests, and it also lets them assert refcounts directly — a real
    /// <c>AssetHandle&lt;T&gt;</c> would need a live operation to say anything about its own.
    ///
    /// LOGS. Several behaviours under test are *defined* as a log line, so the fixture captures
    /// <see cref="Application.logMessageReceived"/> and asserts on the captured text rather than
    /// using <see cref="LogAssert"/>. That way "warned once, not once per Set()" is a countable
    /// assertion instead of an expectation that silently passes when nothing was logged at all.
    /// </remarks>
    [TestFixture]
    public class TieringContractTests
    {
        // ------------------------------------------------------------------ fixture plumbing

        private readonly List<string> _log = new List<string>();
        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        [SetUp]
        public void SetUp()
        {
            _log.Clear();
            Application.logMessageReceived += Capture;
        }

        [TearDown]
        public void TearDown()
        {
            Application.logMessageReceived -= Capture;

            foreach (var disposable in _disposables)
            {
                try { disposable.Dispose(); } catch { /* a test may have disposed it already */ }
            }

            _disposables.Clear();
        }

        private void Capture(string condition, string stackTrace, LogType type) => _log.Add(condition);

        private int CountLogs(string fragment) =>
            _log.Count(line => line.IndexOf(fragment, StringComparison.Ordinal) >= 0);

        /// <summary>
        /// Let this test provoke <c>Debug.LogError</c> without the framework failing it before the
        /// assertion that reads the message can run.
        /// </summary>
        /// <remarks>
        /// MUST be called from the test body. Setting <see cref="LogAssert.ignoreFailingMessages"/>
        /// in <c>[SetUp]</c> looks equivalent and is not: the framework opens the log scope a test
        /// is judged against after <c>[SetUp]</c> has run, so the value lands on the wrong scope and
        /// the test still fails on the first error line. (<c>PoolingRegressionTests</c> sets it in
        /// <c>[SetUp]</c> and fails two tests for exactly this reason.) The framework resets the
        /// flag after each test, so there is nothing to restore.
        ///
        /// This suppresses the framework's own check, not the assertion: every test that calls it
        /// still asserts an exact <see cref="CountLogs"/> on the message it expected.
        /// </remarks>
        private static void ErrorLogsAreExpectedHere() => LogAssert.ignoreFailingMessages = true;

        // ------------------------------------------------------------------ doubles

        /// <summary>
        /// A minimal foreign <see cref="IAssetHandle{T}"/>: real reference counting, no Addressables.
        /// </summary>
        /// <remarks>
        /// <see cref="Retain"/> throws on a dead handle because that is the documented contract the
        /// Wave 1 refcount change introduced, and <c>AssetHandleExtensions.TryRetain</c>'s fallback
        /// for foreign handles is written around it. A double that quietly succeeded there would
        /// make the cache look correct on a path production never takes.
        /// </remarks>
        private sealed class FakeHandle : IAssetHandle<object>
        {
            private int _count = 1;

            public object Asset { get; } = new object();
            public bool IsValid => _count > 0;
            public AsyncOperationStatus Status => AsyncOperationStatus.Succeeded;
            public float Progress => 1f;
            public int ReferenceCount => _count;

            public void Retain()
            {
                if (_count <= 0) throw new ObjectDisposedException(nameof(FakeHandle));
                _count++;
            }

            public void Release()
            {
                if (_count > 0) _count--;
            }

            public AsyncOperationHandle<object> GetHandle() => default;

            public void Dispose() => Release();
        }

        // ------------------------------------------------------------------ builders

        /// <summary>
        /// A config whose only interesting property is its byte budget. Auto-tiering is off so a
        /// clock-driven re-tier cannot move an entry out of the tier a test just put it in.
        /// </summary>
        private static TieredCacheConfig Budget(long max) => new TieredCacheConfig
        {
            MaxCacheSizeBytes = max,
            EvictionTriggerRatio = 0.9f,   // a pass runs once the budget is 90% used
            EvictionTargetRatio = 0.7f,    // ...and aims to leave it at 70%
            EvictionScoreThreshold = 1.0f, // the shipped default; deliberately NOT raised
            EnableAutoTiering = false,
            EnableAutoEviction = true,
            LogTierOperations = false
        };

        private TieredCache<object> NewCache(TieredCacheConfig config)
        {
            var cache = new TieredCache<object>(config);
            _disposables.Add(cache);
            return cache;
        }

        private AssetLoader NewLoader(string scope, TieredCacheConfig tiering = null)
        {
            var loader = tiering == null ? new AssetLoader(scope) : new AssetLoader(scope, tiering);
            _disposables.Add(loader);
            return loader;
        }

        /// <summary>Store one entry and hand back the caller's own reference to it.</summary>
        private static FakeHandle Store(TieredCache<object> cache, string key, long size)
        {
            var handle = new FakeHandle();
            cache.Set(key, handle, size);
            return handle;
        }

        /// <summary>
        /// Make every cached entry evictable, without a clock.
        /// </summary>
        /// <remarks>
        /// <c>Tier == Cold</c> is the first arm of the eviction gate and needs no score at all, so
        /// this is the deterministic equivalent of "these entries have gone cold" — which is the
        /// state eviction is designed for and the only one an EditMode test can reach on demand.
        /// </remarks>
        private static void CoolEverything(TieredCache<object> cache)
        {
            foreach (var entry in cache.GetEntriesByTier(CacheTier.Hot).ToList()) entry.Tier = CacheTier.Cold;
            foreach (var entry in cache.GetEntriesByTier(CacheTier.Warm).ToList()) entry.Tier = CacheTier.Cold;
        }

        // ================================================================== C-9: a pin on a key
        // that is not cached is observable to the caller.
        //
        // The C-agent's mechanism is DEFERRAL, not a bare warning: the pin is remembered and
        // applied when the key arrives, so the pin-then-load order README teaches becomes correct
        // instead of merely being shouted at. It is observable three ways — the additive TryPin
        // return value, the additive PendingPins statistic, and a log line under LogTierOperations.
        // The tests below assert the first two; the third is diagnostics.

        [Test]
        public void C9_TryPin_On_An_Uncached_Key_Reports_The_Deferral()
        {
            var cache = NewCache(Budget(10_000));

            Assert.IsFalse(cache.TryPin("UI/CoreIcon"),
                "false means deferred, not failed. Pin() stays void per invariant 6, so TryPin is " +
                "the additive overload that can say which of the two happened — before C-9 the " +
                "whole public route was void end to end and this call did nothing, silently.");

            var stats = cache.GetStatistics();

            Assert.AreEqual(1, stats.PendingPins,
                "The pin is visible while it waits. A number that never falls to 0 is a pin placed " +
                "on an address that is never loaded.");
            Assert.AreEqual(0, stats.PinnedEntries,
                "...and it is NOT counted as a pinned entry. PinnedEntries alone read 0 both when " +
                "pinning had not happened yet and when it had silently done nothing, which is " +
                "exactly how a lost pin went unnoticed.");
        }

        [Test]
        public void C9_A_Pin_Placed_Before_The_Load_Is_Applied_When_The_Key_Arrives()
        {
            var cache = NewCache(Budget(10_000));

            cache.Pin("UI/CoreIcon");   // the void, README-taught order: pin first, load second

            Store(cache, "UI/CoreIcon", 1000);

            var stats = cache.GetStatistics();

            Assert.AreEqual(1, stats.PinnedEntries,
                "The deferred pin lands on the entry as it enters the cache. This is the half that " +
                "makes README:326-332 correct rather than merely warned about.");
            Assert.AreEqual(0, stats.PendingPins,
                "...and the pending entry is consumed, not left armed to re-pin a later reload.");
        }

        [Test]
        public void C9_TryPin_On_A_Cached_Key_Reports_That_It_Pinned_Right_Now()
        {
            var cache = NewCache(Budget(10_000));
            Store(cache, "UI/CoreIcon", 1000);

            Assert.IsTrue(cache.TryPin("UI/CoreIcon"),
                "true means an entry exists and is pinned as of this call — the other half of the " +
                "answer the void Pin() could never give.");

            var stats = cache.GetStatistics();
            Assert.AreEqual(1, stats.PinnedEntries);
            Assert.AreEqual(0, stats.PendingPins, "Nothing is waiting; the key was already here.");
        }

        [Test]
        public void C9_Unpin_Before_The_Load_Disarms_The_Pending_Pin()
        {
            var cache = NewCache(Budget(10_000));

            cache.Pin("UI/CoreIcon");
            Assert.IsTrue(cache.TryUnpin("UI/CoreIcon"),
                "There was something to undo — the pending pin.");
            Assert.AreEqual(0, cache.GetStatistics().PendingPins);

            Store(cache, "UI/CoreIcon", 1000);

            Assert.AreEqual(0, cache.GetStatistics().PinnedEntries,
                "A Pin/Unpin pair on a key that was never cached must not pin the entry that " +
                "eventually loads. Deferral without cancellation would be a worse version of C-9 " +
                "in the opposite direction: a pin nobody asked for, arriving minutes later.");
        }

        [Test]
        public void C9_TryUnpin_Reports_That_There_Was_Nothing_To_Undo()
        {
            var cache = NewCache(Budget(10_000));

            Assert.IsFalse(cache.TryUnpin("never/pinned"),
                "false is 'this call changed nothing', which is a different answer from 'a pin was " +
                "cancelled' and the caller is entitled to tell them apart.");
        }

        [Test]
        public void C9_A_Pinned_Entry_Is_The_One_Thing_Eviction_Will_Not_Take()
        {
            var cache = NewCache(Budget(10_000));   // trigger at 9000, target 7000

            Store(cache, "a", 2500);
            Store(cache, "b", 2500);
            Store(cache, "c", 2500);                // 7500 used: below the 9000 trigger

            CoolEverything(cache);
            cache.Pin("a");                          // pinned AND cold: only IsPinned can save it

            Store(cache, "d", 2500);                 // 10000 used: a pass runs, wanting 3000 back

            Assert.IsTrue(cache.ContainsKey("a"),
                "IsPinned is eviction's ONLY exemption (C-10 — reference count deliberately is not " +
                "one), so a cold pinned entry surviving is the whole of what pinning buys.");
            Assert.AreEqual(2, cache.GetStatistics().TotalEvictions,
                "b and c paid the 3000 instead, at 2500 each.");
            Assert.AreEqual(1, cache.GetStatistics().PinnedEntries);
        }

        [Test]
        public void C9_A_Deferred_Pin_Is_Applied_Before_The_Eviction_Pass_Its_Own_Insert_Triggers()
        {
            var cache = NewCache(Budget(10_000));

            Store(cache, "a", 2500);
            Store(cache, "b", 2500);
            Store(cache, "c", 2500);
            CoolEverything(cache);

            cache.Pin("critical");                   // nothing cached under it yet
            Store(cache, "critical", 2500);          // arrives, and trips the eviction gate

            var stats = cache.GetStatistics();

            Assert.AreEqual(1, stats.PinnedEntries,
                "Ordering, not luck: Set() applies the pending pin BEFORE running the eviction " +
                "check, or the very entry the caller asked to protect would be a candidate in the " +
                "pass its own insert triggered.");
            Assert.AreEqual(0, stats.PendingPins);
            Assert.IsTrue(cache.ContainsKey("critical"));
        }

        [Test]
        public void C9_A_Pin_Refused_Because_The_Pending_List_Is_Full_Is_An_Error_Not_A_Deferral()
        {
            ErrorLogsAreExpectedHere();

            var cache = NewCache(Budget(10_000));

            for (int i = 0; i < 256; i++) cache.Pin($"never/loaded/{i}");

            Assert.AreEqual(256, cache.GetStatistics().PendingPins,
                "The pending list is bounded; a caller pinning addresses it never loads must not " +
                "grow it for the lifetime of the cache.");

            Assert.IsFalse(cache.TryPin("one/too/many"));
            Assert.AreEqual(256, cache.GetStatistics().PendingPins, "The 257th is not recorded.");

            Assert.AreEqual(1, CountLogs("was refused"),
                "This is the one case where a pin is genuinely refused rather than deferred, so it " +
                "is an error and is NOT gated behind LogTierOperations: nothing later will apply " +
                "it, and the caller would otherwise read the same silence C-9 is about.");
        }

        // ------------------------------------------------------------------ C-9, the same
        // mechanism on the loader that actually ships now.

        [Test]
        public void C9_AssetLoader_PinAsset_On_An_Uncached_Key_Is_Observable_As_A_Pending_Pin()
        {
            var loader = NewLoader("c9-loader", TieredCacheConfig.Default);

            loader.PinAsset<Texture2D>("Boss/Diffuse");

            var stats = loader.GetTieredCacheStats();

            Assert.AreEqual(1, stats.PendingPins);
            Assert.AreEqual(0, stats.PinnedEntries);
        }

        [Test]
        public void C9_AssetLoader_Pending_Pins_Are_Keyed_By_Address_And_Type()
        {
            var loader = NewLoader("c9-keys", TieredCacheConfig.Default);

            loader.PinAsset<Texture2D>("shared/address");

            Assert.AreEqual(1, loader.GetTieredCacheStats<Texture2D>().Value.PendingPins);
            Assert.AreEqual(0, loader.GetTieredCacheStats<Material>().Value.PendingPins,
                "The merged cache spans every Type over one dictionary, so a pin keyed by address " +
                "alone would pin whichever type happened to arrive first — which is why this " +
                "loader's pending set is keyed by (address, Type) and TieredCache<T>'s, being " +
                "single-typed already, is not.");
            Assert.AreEqual(1, loader.GetTieredCacheStats().PendingPins,
                "The unfiltered figure counts it once.");
        }

        [Test]
        public void C9_AssetLoader_UnpinAsset_Cancels_A_Pending_Pin()
        {
            var loader = NewLoader("c9-unpin", TieredCacheConfig.Default);

            loader.PinAsset<Texture2D>("Boss/Diffuse");
            loader.UnpinAsset<Texture2D>("Boss/Diffuse");

            Assert.AreEqual(0, loader.GetTieredCacheStats().PendingPins);
        }

        [Test]
        public void C9_PinAsset_On_An_Untiered_Loader_Says_So_Instead_Of_Failing_Quietly()
        {
            var loader = NewLoader("c9-untiered");

            Assert.IsFalse(loader.TieringEnabled);

            loader.PinAsset<Texture2D>("Boss/Diffuse");

            Assert.AreEqual(1, CountLogs("was built without tiering"),
                "A pin on an untiered loader cannot protect anything because nothing evicts. That " +
                "is the exact shape of the bug C-9 describes, so it warns rather than deferring a " +
                "pin no eviction pass will ever consult.");
            Assert.AreEqual(0, loader.GetTieredCacheStats().PendingPins,
                "...and it records nothing, so PendingPins cannot grow on a loader that has no use " +
                "for it.");
            Assert.IsNull(loader.GetTieredCacheStats<Texture2D>(),
                "null from the per-type overload means exactly one thing: tiering is off.");
        }

        [Test]
        public void C9_PinAsset_Rejects_An_Empty_Address_Rather_Than_Recording_It()
        {
            var loader = NewLoader("c9-null", TieredCacheConfig.Default);

            Assert.Throws<ArgumentNullException>(() => loader.PinAsset<Texture2D>(null));
            Assert.Throws<ArgumentNullException>(() => loader.PinAsset<Texture2D>(string.Empty));
            Assert.AreEqual(0, loader.GetTieredCacheStats().PendingPins);
        }

        // ================================================================== C-13: an eviction pass
        // chases a number it can actually reach, and reports the case where it cannot.
        //
        // The correction C-13 carries matters as much as the fix: a brand-new entry is NOT at risk
        // from the pass its own insert triggers (it is born Hot with AccessCount 1, scoring ~69.3
        // against thresholds of 1.0/3.0/0.5). What was real is the other end — an insert larger
        // than Max * EvictionTargetRatio puts the cache in a state no pass can resolve, and the
        // drain loop used to consume every candidate chasing a demand it could never meet, then log
        // "eviction complete" with a figure nobody compared to anything.

        [Test]
        public void C13_An_Eviction_Pass_Stops_As_Soon_As_It_Has_Made_Room()
        {
            var cache = NewCache(Budget(10_000));   // trigger 9000, target 7000

            var a = Store(cache, "a", 2500);
            var b = Store(cache, "b", 2500);
            var c = Store(cache, "c", 2500);        // 7500 used, still under the trigger
            CoolEverything(cache);

            Store(cache, "d", 2500);                // 10000 used -> a pass runs, overage 3000

            var stats = cache.GetStatistics();

            Assert.AreEqual(2, stats.TotalEvictions,
                "Two entries at 2500 cover the 3000 the cache is over by. A pass that drained its " +
                "whole candidate list — the pre-C-13 behaviour when the demand was unreachable — " +
                "would report 3 and leave the cache emptier than the target asked for.");
            Assert.AreEqual(5000, stats.TotalSizeBytes);
            Assert.Less(stats.TotalSizeBytes, 7000L,
                "Room was actually made: the cache is back under its post-eviction target.");
            Assert.AreEqual(0, CountLogs("Eviction cannot reach its target"),
                "A pass that reached its target must not warn.");

            Assert.IsTrue(cache.ContainsKey("d"),
                "The entry whose insert triggered the pass survives it. That immunity is the ~69.3 " +
                "score a fresh entry is born with, and it is a safety margin rather than a " +
                "coincidence — raising EvictionScoreThreshold past it arms exactly the failure the " +
                "original work order imagined.");

            int survivors = new[] { a, b, c }.Count(h => h.ReferenceCount == 2);
            Assert.AreEqual(1, survivors, "One of the three still has the cache's reference on it.");
        }

        [Test]
        public void C13_Eviction_Gives_Back_Only_The_Caches_Own_Reference()
        {
            var cache = NewCache(Budget(10_000));

            var doomed = Store(cache, "doomed", 5000);
            Assert.AreEqual(2, doomed.ReferenceCount,
                "One reference is the caller's, one is the cache's own — taken via TryRetain in " +
                "Set(). Before C-1 the cache stored the handle without taking anything, then " +
                "released the caller's reference on the way out.");

            CoolEverything(cache);
            Store(cache, "trigger", 5000);          // 10000 used -> a pass runs, overage 3000

            Assert.IsFalse(cache.ContainsKey("doomed"));
            Assert.AreEqual(1, doomed.ReferenceCount,
                "Eviction is a decrement, not a hard release: it gives back the cache's reference " +
                "and nothing else.");
            Assert.IsTrue(doomed.IsValid,
                "A caller still holding the handle across an eviction keeps a live asset. Eviction " +
                "freeing an asset under a live holder is reserved for ClearCache/teardown, and " +
                "doing it here is the use-after-free C-1 was about.");
        }

        [Test]
        public void C13_An_Insert_Larger_Than_The_Eviction_Target_Is_Warned_About_At_Admission()
        {
            var cache = NewCache(Budget(10_000));   // post-eviction target is 7000

            Store(cache, "oversized", 9000);

            Assert.AreEqual(1, CountLogs("on its own, larger than the"),
                "The admission half of C-13. An entry bigger than Max * EvictionTargetRatio puts " +
                "the cache into a state eviction cannot resolve while it stays cached, and it is " +
                "deliberately not gated behind LogTierOperations because the entry IS cached — " +
                "nothing else will ever surface it.");
            Assert.IsTrue(cache.ContainsKey("oversized"),
                "Warned about, not refused: refusing it would be a silent load failure instead.");
        }

        [Test]
        public void C13_A_Pass_That_Cannot_Reach_Its_Target_Says_So_Once_Per_Episode()
        {
            var cache = NewCache(Budget(10_000));   // trigger 9000, target 7000

            var small1 = Store(cache, "small-1", 2000);
            var small2 = Store(cache, "small-2", 2000);
            CoolEverything(cache);

            // 13000 used against a 7000 target: the pass wants 6000 back and can only ever reach
            // the 4000 held by the two cold entries. The 9000 entry is Hot and freshly inserted, so
            // the gate excludes it — correctly, and permanently until it cools.
            Store(cache, "huge", 9000);

            Assert.AreEqual(1, small1.ReferenceCount, "small-1 was evicted (decremented).");
            Assert.AreEqual(1, small2.ReferenceCount, "small-2 too — the whole reclaimable pool.");

            var stats = cache.GetStatistics();
            Assert.AreEqual(2, stats.TotalEvictions);
            Assert.AreEqual(9000, stats.TotalSizeBytes,
                "Still over target, and no further pass can change that while 'huge' is cached.");

            Assert.AreEqual(1, CountLogs("Eviction cannot reach its target"),
                "The residue the pass could not take is reported instead of disappearing into an " +
                "'eviction complete' line with a number nobody compares to the target.");

            // 9500 used trips the 9000 gate again, and the pass fails again for the same reason:
            // nothing is cold, so nothing is reclaimable. The caller has already been told, and
            // once per load forever is spam rather than a signal.
            Store(cache, "another", 500);

            Assert.AreEqual(9500, cache.GetStatistics().TotalSizeBytes,
                "Confirming the second pass really did run and really did reclaim nothing — a " +
                "latch test whose second pass quietly succeeded would pass for the wrong reason.");
            Assert.AreEqual(1, CountLogs("Eviction cannot reach its target"),
                "Latched: reported once per over-budget episode, not once per Set().");
        }

        [Test]
        public void C13_The_Shortfall_Latch_Clears_Once_A_Pass_Ends_Back_Under_Target()
        {
            var cache = NewCache(Budget(10_000));

            var small1 = Store(cache, "small-1", 2000);
            var small2 = Store(cache, "small-2", 2000);
            CoolEverything(cache);
            Store(cache, "huge", 9000);

            Assert.AreEqual(1, CountLogs("Eviction cannot reach its target"));

            // Take the unreachable entry out by hand: the cache is now healthy again.
            Assert.IsTrue(cache.Remove("huge"));

            var refilled1 = Store(cache, "refill-1", 4000);
            var refilled2 = Store(cache, "refill-2", 4000);
            CoolEverything(cache);
            Store(cache, "trip", 2000);   // 10000 used -> a pass runs and this time it succeeds

            Assert.LessOrEqual(cache.GetStatistics().TotalSizeBytes, 7000L);
            Assert.AreEqual(1, CountLogs("Eviction cannot reach its target"),
                "A healthy pass clears the latch rather than adding to the count.");

            // ...and a genuinely new episode is reported again, which is what makes the latch a
            // de-duplicator rather than a mute button.
            CoolEverything(cache);
            Store(cache, "huge-again", 9500);

            Assert.AreEqual(2, CountLogs("Eviction cannot reach its target"),
                "Once per episode means the NEXT episode is reported too.");

            GC.KeepAlive(small1);
            GC.KeepAlive(small2);
            GC.KeepAlive(refilled1);
            GC.KeepAlive(refilled2);
        }

        [Test]
        public void C13_An_Unlimited_Budget_Never_Runs_A_Pass()
        {
            var config = Budget(10_000);
            config.MaxCacheSizeBytes = 0;   // 0 == unlimited, per TieredCacheConfig

            var cache = NewCache(config);

            Store(cache, "a", 500_000);
            Store(cache, "b", 500_000);

            var stats = cache.GetStatistics();
            Assert.AreEqual(0, stats.TotalEvictions);
            Assert.AreEqual(2, stats.TotalEntries);
            Assert.AreEqual(0, CountLogs("on its own, larger than the"),
                "There is no target to be larger than when the budget is unlimited.");
        }

        // ------------------------------------------------------------------ C-13 on AssetLoader's
        // built-in tiering — the implementation that actually ships now.
        //
        // WHITE-BOX, AND WHY. AssetLoader's only insert path is CacheHandle<T>, reachable only from
        // a completed Addressables load, which an EditMode test has no content for. The eviction
        // sweep it feeds is the code every tiered loader now runs, so leaving it uncovered because
        // the front door needs content would be covering the retired implementation and not the
        // live one. LoaderCacheProbe plants entries with the real internal CachedAsset/IOwnedHandle
        // types; the pass is then triggered through the PUBLIC ForceEviction().

        [Test]
        public void C13_AssetLoader_Eviction_Stops_As_Soon_As_It_Has_Made_Room()
        {
            var loader = NewLoader("c13-loader", Budget(10_000));   // target 7000

            var a = Plant(loader, "a", 2500, CacheTier.Cold);
            var b = Plant(loader, "b", 2500, CacheTier.Cold);
            var c = Plant(loader, "c", 2500, CacheTier.Cold);
            var fresh = Plant(loader, "fresh", 2500, CacheTier.Hot);
            SetTieredBytes(loader, 10_000);

            loader.ForceEviction();

            var stats = loader.GetTieredCacheStats();

            Assert.AreEqual(2, stats.TotalEvictions,
                "Overage is 3000 and each cold entry is 2500, so two of them cover it. Draining " +
                "all three would be the pre-C-13 shape.");
            Assert.AreEqual(5000, stats.TotalSizeBytes,
                "_tieredBytes is adjusted by RemoveCacheEntry and nowhere else, so the reported " +
                "figure and the enforced figure cannot drift.");
            Assert.AreEqual(2, stats.TotalEntries);

            Assert.IsTrue(Contains(loader, "fresh"),
                "The Hot entry scores far above EvictionScoreThreshold and is never a candidate.");
            Assert.AreEqual(0, fresh.Releases + fresh.ForceReleases);

            var evicted = new[] { a, b, c }.Where(h => h.Releases > 0).ToArray();
            Assert.AreEqual(2, evicted.Length);
            Assert.IsTrue(evicted.All(h => h.ForceReleases == 0),
                "Eviction is Dispose() — a decrement — never ForceRelease(). Freeing an asset " +
                "under a live holder is reserved for ClearCache, ReleaseAsset and teardown.");
        }

        [Test]
        public void C13_AssetLoader_Reports_A_Pass_It_Cannot_Complete_Once_Per_Episode()
        {
            var loader = NewLoader("c13-shortfall", Budget(10_000));   // target 7000

            var small = Plant(loader, "small", 2000, CacheTier.Cold);
            Plant(loader, "huge", 9000, CacheTier.Hot);
            SetTieredBytes(loader, 11_000);

            loader.ForceEviction();

            Assert.AreEqual(1, small.Releases, "The whole reclaimable pool was taken...");
            Assert.AreEqual(9000, loader.GetTieredCacheStats().TotalSizeBytes,
                "...and the cache is still over target, because the rest is held by an entry no " +
                "pass will take.");
            Assert.AreEqual(1, CountLogs("Eviction cannot reach its target"));

            loader.ForceEviction();
            loader.ForceEviction();

            Assert.AreEqual(1, CountLogs("Eviction cannot reach its target"),
                "Latched. Under the pump this method runs every five seconds forever; once per " +
                "pass would bury the log it is trying to be.");
        }

        [Test]
        public void C13_AssetLoader_Eviction_Skips_A_Pinned_Entry_Whatever_Its_Tier()
        {
            var loader = NewLoader("c13-pinned", Budget(10_000));

            var pinned = Plant(loader, "pinned", 5000, CacheTier.Cold);
            var plain = Plant(loader, "plain", 5000, CacheTier.Cold);
            Entry(loader, "pinned").IsPinned = true;
            SetTieredBytes(loader, 10_000);

            loader.ForceEviction();

            Assert.AreEqual(0, pinned.Releases + pinned.ForceReleases,
                "IsPinned is the only exemption the candidate filter honours.");
            Assert.AreEqual(1, plain.Releases);
            Assert.AreEqual(1, loader.GetTieredCacheStats().PinnedEntries);
        }

        [Test]
        public void C13_ForceEviction_On_An_Untiered_Loader_Is_A_No_Op()
        {
            var loader = NewLoader("c13-untiered");

            var planted = Plant(loader, "a", 5000, CacheTier.Cold);
            SetTieredBytes(loader, 5000);

            Assert.DoesNotThrow(() => loader.ForceEviction(),
                "The registry pump walks every live loader, tiered or not — an untiered one must " +
                "cost one call and touch nothing, which is what makes a second list of 'the tiered " +
                "ones' unnecessary.");
            Assert.AreEqual(0, planted.Releases + planted.ForceReleases);
        }
    }
}
