using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AddressableManager.Core;
using AddressableManager.Loaders;
using AddressableManager.Pooling;
using AddressableManager.Pooling.Adapters;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.TestTools;

namespace AddressableManager.Tests.Editor
{
    /// <summary>
    /// The regression guard HANDOFF_TO_SESSION_B.md §8.2 asks for: the three
    /// <c>UnityEngine.Pool.ObjectPool</c> behaviours P-3/P-4/P-7 were designed around, plus the
    /// parts of commit <c>c004344</c> that EditMode can honestly reach.
    /// </summary>
    /// <remarks>
    /// WHY THIS FILE EXISTS
    ///
    /// §8.2 lists three facts about <c>UnityEngine.Pool.ObjectPool&lt;T&gt;</c> — the constructor
    /// rejects <c>maxSize &lt;= 0</c>; <c>CountAll</c> only rises inside <c>Get()</c>;
    /// <c>collectionCheck</c> only throws for an instance already on the inactive list — and records
    /// that all three came from knowledge of Unity's source rather than from anything in this repo.
    /// P-3, P-4 and P-7 were each designed against them. A design resting on three unverified claims
    /// about a third-party type is one Unity patch release away from being wrong silently, so they
    /// are asserted here directly against the real type. All three hold as described; see the
    /// individual tests for the consequence each one carries.
    ///
    /// WHAT IS DELIBERATELY NOT HERE
    ///
    /// No GC or timing assertions, following <see cref="AssetLoaderRegistryTests"/>: nothing below
    /// depends on when a finaliser runs, on frame boundaries, or on wall-clock elapsed time. The
    /// auto-resize state machine is driven by <c>Time.realtimeSinceStartup</c> and
    /// <c>ShrinkDelaySeconds</c>, so every pool built here sets <c>EnableAutoResize = false</c> and
    /// calls <see cref="DynamicPool{T}.ResizeTo"/> explicitly — the accounting is the subject, the
    /// clock is not.
    ///
    /// No PlayMode tests. §8.2 records that P-2 and P-6 need a <c>LoadSceneMode.Single</c> boundary
    /// and that EditMode cannot reproduce that destroy, so nothing here pretends to. The one place
    /// this bites is P-7: <see cref="AddressablePoolManager"/> can only acquire a pool through
    /// <c>CreatePoolAsync</c>, which needs real Addressables content, so the branch of
    /// <c>Despawn</c> that runs *with* a pool registered is out of reach from EditMode. What is
    /// covered instead is stated on each P-7 test, together with what it does not cover.
    ///
    /// ON EXPECTED LOG OUTPUT
    ///
    /// Several behaviours under test are defined as "log and return a neutral value", so they are
    /// noisy by design. Warnings do not fail a test, so the P-26 clamp warning and
    /// <c>Despawn</c>'s refusal warnings need no ceremony. Errors do fail a test, and the one error
    /// expected here is declared with <see cref="LogAssert.Expect(LogType, Regex)"/> rather than
    /// waved away with <c>LogAssert.ignoreFailingMessages</c> — declaring it keeps the test failing
    /// if some *different* error appears at that point.
    ///
    /// That choice is not stylistic. On this Unity/test-framework version
    /// (6000.5.7f1, com.unity.test-framework@1405238725ab) setting
    /// <c>LogAssert.ignoreFailingMessages = true</c> from a <c>[SetUp]</c> method does NOT suppress
    /// an Error-level unhandled log message; the test still fails with "Unhandled log message ...
    /// Use UnityEngine.TestTools.LogAssert.Expect". Verified by running it. Three tests in other
    /// fixtures currently fail for exactly this reason — see this session's report.
    ///
    /// OVERLAP NOTE: <c>PoolingRegressionTests.cs</c> (a concurrent session's work, untracked at the
    /// time of writing) also asserts the three §8.2 characterisations. They are kept here too
    /// because that file is uncommitted work in progress against a different work order, and
    /// because a duplicated assertion about a third-party invariant costs a millisecond. If both
    /// files land, dropping one copy is a safe cleanup.
    /// </remarks>
    [TestFixture]
    public class PoolAndBudgetContractTests
    {
        /// <summary>A plain POCO, so pool accounting can be tested without Unity object lifetime.</summary>
        private sealed class Item
        {
            public int Id;
        }

        private sealed class BudgetAssetA { }

        private sealed class BudgetAssetB { }

        private readonly List<GameObject> _spawnedObjects = new List<GameObject>();
        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        [TearDown]
        public void TearDown()
        {
            foreach (var disposable in _disposables)
            {
                try { disposable.Dispose(); } catch { /* a test may have disposed it already */ }
            }
            _disposables.Clear();

            foreach (var go in _spawnedObjects)
            {
                // Unity equality: skip anything already destroyed. DestroyImmediate, not Destroy —
                // this is EditMode, where Destroy is refused and the object would survive the run.
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            }
            _spawnedObjects.Clear();
        }

        private GameObject NewGameObject(string name)
        {
            var go = new GameObject(name);
            _spawnedObjects.Add(go);
            return go;
        }

        private T Track<T>(T disposable) where T : IDisposable
        {
            _disposables.Add(disposable);
            return disposable;
        }

        // =================================================================== §8.2, claim (a)

        [Test]
        public void ObjectPool_Constructor_Throws_When_MaxSize_Is_Not_Positive()
        {
            // The claim P-7's "one documented meaning for maxSize <= 0" rests on. UnityPoolAdapter
            // translates a non-positive maxSize to int.MaxValue ("unlimited") *before* handing it to
            // ObjectPool precisely because passing it straight through would throw here. If this
            // ever stopped throwing, that translation would look like pointless ceremony and the
            // next reader would delete it — reintroducing the adapter divergence P-7 closed, where
            // maxSize == 0 meant "unlimited" to CustomPoolAdapter and "crash" to this one.
            Assert.Throws<ArgumentException>(
                () => new UnityEngine.Pool.ObjectPool<Item>(() => new Item(), maxSize: 0),
                "maxSize 0 must be rejected by the constructor");

            Assert.Throws<ArgumentException>(
                () => new UnityEngine.Pool.ObjectPool<Item>(() => new Item(), maxSize: -1),
                "a negative maxSize must be rejected by the constructor");

            Assert.DoesNotThrow(
                () => new UnityEngine.Pool.ObjectPool<Item>(() => new Item(), maxSize: 1),
                "the boundary is <= 0, not < 1 — a maxSize of exactly 1 must be accepted");
        }

        // =================================================================== §8.2, claim (b)

        [Test]
        public void ObjectPool_CountAll_Rises_Only_Inside_Get()
        {
            var pool = new UnityEngine.Pool.ObjectPool<Item>(() => new Item(), maxSize: 10);

            Assert.AreEqual(0, pool.CountAll, "a fresh pool has constructed nothing");

            var first = pool.Get();
            Assert.AreEqual(1, pool.CountAll, "a Get() that had to construct raises CountAll");

            pool.Release(first);
            Assert.AreEqual(1, pool.CountAll, "Release() never raises CountAll");

            var reused = pool.Get();
            Assert.AreEqual(1, pool.CountAll, "a Get() served from the free list does not raise it either");
            Assert.AreSame(first, reused, "and it is the same instance coming back");

            pool.Get();
            Assert.AreEqual(2, pool.CountAll, "only a Get() with an empty free list constructs");
        }

        [Test]
        public void ObjectPool_CountActive_Goes_Negative_When_Released_Without_A_Matching_Get()
        {
            // This is the mechanism behind P-4, stated as a property of the type rather than as a
            // claim about it. CountActive is derived (CountAll - CountInactive) and CountAll can
            // only be raised by Get(), so seeding the free list with Release() — which is exactly
            // what DynamicPool.GrowPool used to do via `_innerPool.Release(_createFunc())` — pushes
            // the derived figure below zero, one unit per seeded instance.
            //
            // That negative number is what reached Standard.GetPoolStats, and, worse, what fed
            // DynamicPool's own usageRatio: negative ratios never clear GrowThreshold, so growth
            // switched off permanently while shrink fired unconditionally.
            var pool = new UnityEngine.Pool.ObjectPool<Item>(() => new Item(), maxSize: 10);

            pool.Release(new Item());

            Assert.AreEqual(0, pool.CountAll, "the seeded instance never went through Get()");
            Assert.AreEqual(1, pool.CountInactive, "but it is on the free list");
            Assert.AreEqual(-1, pool.CountActive,
                "so the derived active count is negative — the type permits it, which is why the " +
                "adapters must not let a pre-populate be expressed as a bare Release()");
        }

        // =================================================================== §8.2, claim (c)

        [Test]
        public void ObjectPool_CollectionCheck_Throws_Only_For_An_Instance_Already_On_The_Free_List()
        {
            var pool = new UnityEngine.Pool.ObjectPool<Item>(() => new Item(), collectionCheck: true);

            var borrowed = pool.Get();
            pool.Release(borrowed);

            Assert.Throws<InvalidOperationException>(() => pool.Release(borrowed),
                "releasing an instance that is already on the free list is the one case " +
                "collectionCheck catches");

            // The other half, and the more important one: collectionCheck scans only this pool's own
            // inactive list, so it has no idea which instances this pool handed out. A stranger is
            // adopted in silence. That is why P-7 had to verify ownership up in
            // AddressablePoolManager.Despawn rather than trusting the adapter to reject a
            // wrong-pool instance.
            var stranger = new Item { Id = 99 };
            Assert.DoesNotThrow(() => pool.Release(stranger),
                "collectionCheck does not know the pool's own hand-outs, only its free list");
            Assert.AreEqual(2, pool.CountInactive,
                "and the stranger is now sitting in the free list, ready to be handed to the next " +
                "caller as though this pool had made it");
        }

        // =================================================================== P-3

        [Test]
        public void Preload_Creates_The_Requested_Count_And_Not_One()
        {
            // The bug, first, so the guard below is anchored to something real. The original preload
            // was `for (i < N) { var x = pool.Get(); pool.Release(x); }`. Loop 0 constructs; every
            // later loop finds the instance it just released sitting on the free list and reuses it.
            // preloadCount 50 produced one instance and a log line claiming fifty.
            int constructedByTheOldShape = 0;
            var raw = new UnityEngine.Pool.ObjectPool<Item>(
                () => { constructedByTheOldShape++; return new Item(); }, maxSize: 100);

            for (int i = 0; i < 5; i++)
            {
                var borrowed = raw.Get();
                raw.Release(borrowed);
            }

            Assert.AreEqual(1, constructedByTheOldShape,
                "P-3: a Get/Release pair per iteration constructs exactly once, no matter the count");

            // The fix: both adapters route preload through the Prewarm primitive, which holds every
            // instance until the whole batch exists and only then releases them, so each Get() has
            // to miss the free list.
            AssertPrewarmCreatesDistinctInstances(
                createFunc => new UnityPoolAdapter<Item>(createFunc, maxSize: 100), "UnityPoolAdapter");

            AssertPrewarmCreatesDistinctInstances(
                createFunc => new CustomPoolAdapter<Item>(createFunc, maxSize: 100), "CustomPoolAdapter");
        }

        private void AssertPrewarmCreatesDistinctInstances(
            Func<Func<Item>, IObjectPool<Item>> build, string adapterName)
        {
            var created = new List<Item>();
            var pool = Track(build(() => { var item = new Item { Id = created.Count }; created.Add(item); return item; }));

            ((IResizablePool<Item>)pool).Prewarm(5);

            Assert.AreEqual(5, created.Count,
                $"{adapterName}: preload must construct the count it was asked for");
            Assert.AreEqual(5, new HashSet<Item>(created).Count,
                $"{adapterName}: and they must be five distinct instances, not one instance five times");
            Assert.AreEqual((0, 5), pool.GetStats(),
                $"{adapterName}: all five sit in the free list and none counts as borrowed — a " +
                "preload that registered as usage would skew the auto-resize peak it feeds");
        }

        // =================================================================== P-4

        [Test]
        public void ResizeTo_Up_Then_Down_Never_Drives_ActiveCount_Negative_On_Either_Adapter()
        {
            // The regression test HANDOFF_TO_SESSION_B.md P-4 asks for by name: "activeCount >= 0
            // after ResizeTo up then down, on both adapters". Grow used to Release() instances the
            // inner pool never handed out (driving CountActive negative — see
            // ObjectPool_CountActive_Goes_Negative_...) and shrink used to Get() and destroy without
            // ever releasing (inflating it by one per trimmed slot, permanently). One root cause,
            // two opposite symptoms, so a test that only resizes one way would miss half of it.
            AssertResizeKeepsBooks(
                config => new UnityPoolAdapter<Item>(() => new Item(), maxSize: config.MaxSize),
                "UnityPoolAdapter");

            AssertResizeKeepsBooks(
                config => new CustomPoolAdapter<Item>(() => new Item(), maxSize: config.MaxSize),
                "CustomPoolAdapter");
        }

        private void AssertResizeKeepsBooks(
            Func<DynamicPoolConfig, IObjectPool<Item>> buildInner, string adapterName)
        {
            var config = DynamicPoolConfig.Default;   // InitialCapacity 10, MinSize 5, MaxSize 100
            // The clock plays no part in what this test asserts, so it is switched off rather than
            // waited on: ResizeTo is a direct order and ignores this flag.
            config.EnableAutoResize = false;

            var pool = Track(new DynamicPool<Item>(
                buildInner(config), config, () => new Item(), _ => { }, $"p4-{adapterName}"));

            pool.ResizeTo(20);
            Assert.AreEqual((0, 10), pool.GetStats(),
                $"{adapterName}: growing the budget from 10 to 20 pre-populates the ten new slots " +
                "and none of them counts as borrowed");

            var borrowed = new[] { pool.Get(), pool.Get(), pool.Get() };
            Assert.AreEqual((3, 7), pool.GetStats(), $"{adapterName}: three out, seven left");

            // Down past what the free list can supply: the shrink is capped at the seven pooled
            // instances and must not touch the three the caller is still holding.
            pool.ResizeTo(5);

            var afterShrink = pool.GetStats();
            Assert.GreaterOrEqual(afterShrink.activeCount, 0,
                $"{adapterName}: P-4 — the active count must never go negative, because it is " +
                "DynamicPool's own usageRatio numerator and a negative ratio inverts auto-resize");
            Assert.AreEqual((3, 0), afterShrink,
                $"{adapterName}: the three borrowed instances survive the shrink and are still " +
                "counted; the free list is emptied");

            var dynamicStats = pool.GetDynamicStats();
            Assert.AreEqual(3, dynamicStats.ActiveCount,
                $"{adapterName}: the same number must reach GetDynamicStats, which is what " +
                "Standard.GetPoolStats and the resize heuristics both read");
            Assert.GreaterOrEqual(dynamicStats.UsageRatio, 0f,
                $"{adapterName}: a negative usage ratio is what permanently disabled the grow branch");

            foreach (var item in borrowed) pool.Release(item);

            Assert.AreEqual((0, 3), pool.GetStats(),
                $"{adapterName}: and returning them balances the books exactly — no phantom active " +
                "left over from the trim, which is the half that used to inflate the count forever");
        }

        // =================================================================== P-7

        [Test]
        public void Raw_Adapters_Disagree_About_A_Stranger_And_A_Double_Release()
        {
            // This is the divergence P-7 exists to neutralise, pinned so that "Despawn verifies
            // ownership itself" keeps its justification. Nothing below is a defect in the adapters —
            // it is the reason the identity check cannot live in them.
            var unity = Track(new UnityPoolAdapter<Item>(() => new Item(), maxSize: 100));
            var unityBorrowed = unity.Get();
            unity.Release(unityBorrowed);

            Assert.Throws<InvalidOperationException>(() => unity.Release(unityBorrowed),
                "UnityPoolAdapter forwards to ObjectPool's collectionCheck, which throws");

            Assert.DoesNotThrow(() => unity.Release(new Item()),
                "but a stranger is adopted without a word");
            Assert.AreEqual(2, unity.GetStats().pooledCount,
                "and it is now in the free list, reachable by the next Get() for this address");

            var custom = Track(new CustomPoolAdapter<Item>(() => new Item(), maxSize: 100));
            var customBorrowed = custom.Get();
            custom.Release(customBorrowed);

            Assert.DoesNotThrow(() => custom.Release(customBorrowed),
                "CustomPoolAdapter keeps a real membership set, so a double release is refused, " +
                "not thrown on — the opposite reaction to the same caller mistake");

            Assert.DoesNotThrow(() => custom.Release(new Item()));
            Assert.AreEqual(1, custom.GetStats().pooledCount,
                "and a stranger is rejected rather than adopted");
        }

        [Test]
        public void Despawn_Of_An_Unknown_Instance_Behaves_Identically_Under_Both_Factories()
        {
            // P-7, as far as EditMode can honestly reach it. AddressablePoolManager only acquires a
            // pool through CreatePoolAsync, which needs real Addressables content, so this covers
            // the reject-before-the-adapter branches only: no pool registered for the address, and
            // an instance this manager never handed out. Those are the two Despawn arguments a
            // caller most often gets wrong, and the previous test shows the two adapters answering
            // them in three different ways when asked directly.
            //
            // NOT COVERED, and needing PlayMode: Despawn with a pool registered for the address, in
            // particular the wrong-pool case where the instance belongs to a *different* live pool.
            //
            // The outcome is compared between factories rather than asserted against a hardcoded
            // expectation, because "the same thing happens either way" is the actual P-7 claim. It
            // is then also asserted to be a normal return, so the test cannot pass by having both
            // sides fail identically.
            var unityManager = Track(new AddressablePoolManager(
                Track(new AssetLoader("p7-unity")), new UnityPoolFactory()));
            var customManager = Track(new AddressablePoolManager(
                Track(new AssetLoader("p7-custom")), new CustomPoolFactory()));

            var unityStranger = NewGameObject("p7-stranger-unity");
            var customStranger = NewGameObject("p7-stranger-custom");

            // Both paths end in UnityEngine.Object.Destroy, which the engine refuses in EditMode
            // with an error and a no-op. That refusal is an artefact of running outside play mode,
            // not behaviour of the code under test, so it is declared rather than suppressed: if
            // Despawn ever logs some *other* error here, this test must still fail. It also means
            // the instance survives the call, so nothing below asserts that it was destroyed —
            // that half genuinely needs PlayMode.
            var editModeDestroy = new Regex("Destroy may not be called from edit mode");

            LogAssert.Expect(LogType.Error, editModeDestroy);
            string unityOutcome = OutcomeOf(() => unityManager.Despawn("pool/never-created", unityStranger));

            LogAssert.Expect(LogType.Error, editModeDestroy);
            string customOutcome = OutcomeOf(() => customManager.Despawn("pool/never-created", customStranger));

            Assert.AreEqual(unityOutcome, customOutcome,
                "P-7: an instance no pool owns must be handled the same way whichever IPoolFactory " +
                "the Facade happened to install — the choice is made in AddressablesFacade, far " +
                "from this call site");
            Assert.AreEqual("returned", unityOutcome,
                "and it must be a refusal, not an exception: Despawn is called from gameplay " +
                "teardown where nothing can handle one");
        }

        [Test]
        public void Despawn_Of_An_Already_Destroyed_Instance_Is_Refused_Not_Thrown_On()
        {
            // The other half of P-7's "already despawned" row, reachable in EditMode because this
            // branch settles the books and returns without asking any adapter anything.
            //
            // DestroyImmediate is a genuine destroy, so `instance == null` is genuinely a Unity
            // fake-null here — this is not a stand-in for the scene-unload destroy §8.2 reserves
            // for PlayMode, it is the same managed state arrived at by a supported EditMode route.
            var unityManager = Track(new AddressablePoolManager(
                Track(new AssetLoader("p7-destroyed-unity")), new UnityPoolFactory()));
            var customManager = Track(new AddressablePoolManager(
                Track(new AssetLoader("p7-destroyed-custom")), new CustomPoolFactory()));

            var unityCorpse = new GameObject("p7-corpse-unity");
            var customCorpse = new GameObject("p7-corpse-custom");
            UnityEngine.Object.DestroyImmediate(unityCorpse);
            UnityEngine.Object.DestroyImmediate(customCorpse);

            string unityOutcome = OutcomeOf(() => unityManager.Despawn("pool/never-created", unityCorpse));
            string customOutcome = OutcomeOf(() => customManager.Despawn("pool/never-created", customCorpse));

            Assert.AreEqual("returned", unityOutcome,
                "P-8/P-7: a destroyed instance reaching Despawn is a bookkeeping event, not an error");
            Assert.AreEqual(unityOutcome, customOutcome, "and it must not depend on the factory");

            // A genuine null is a different situation from a destroyed instance, and must also be
            // survivable — Despawn separates the two with ReferenceEquals before Unity equality.
            Assert.AreEqual("returned", OutcomeOf(() => unityManager.Despawn("pool/never-created", null)));
        }

        private static string OutcomeOf(Action action)
        {
            try
            {
                action();
                return "returned";
            }
            catch (Exception ex)
            {
                return ex.GetType().Name;
            }
        }

        // =================================================================== PoolInstanceGuard

        /// <summary>
        /// A deliberately naive check, standing in for the code <see cref="PoolInstanceGuard"/>
        /// replaced. Inside an unconstrained generic, <c>obj == null</c> binds to reference
        /// equality — Unity's overridden operator is not in scope for <c>T</c>.
        /// </summary>
        private static bool NaiveNullCheck<T>(T obj) where T : class
        {
            return obj == null;
        }

        [Test]
        public void PoolInstanceGuard_Sees_A_Destroyed_UnityObject_That_A_Generic_Null_Check_Misses()
        {
            var live = NewGameObject("guard-live");
            Assert.IsFalse(PoolInstanceGuard.IsDestroyed(live),
                "a live GameObject is not destroyed");

            var doomed = new GameObject("guard-doomed");
            UnityEngine.Object.DestroyImmediate(doomed);

            Assert.IsTrue(PoolInstanceGuard.IsDestroyed(doomed),
                "P-2: a destroyed UnityEngine.Object must be recognised, or the pool hands a corpse " +
                "to a caller and the MissingReferenceException surfaces inside whichever callback " +
                "touches it first — nowhere near the pool");

            // The trap the guard exists for, demonstrated rather than described. Both adapters used
            // to test pooled instances this way from inside their own generic methods.
            Assert.IsFalse(NaiveNullCheck(doomed),
                "the same destroyed object reads as 'not null' through a generic == check, because " +
                "T is unconstrained and Unity's operator== never enters the picture");

            Assert.IsFalse(PoolInstanceGuard.IsDestroyed(new Item()),
                "a plain object is never 'destroyed' — the guard must not claim otherwise for the " +
                "POCO pools this package also supports");

            Assert.IsFalse(PoolInstanceGuard.IsDestroyed<Item>(null),
                "and a genuine null is a different question, answered elsewhere: Despawn checks " +
                "ReferenceEquals(x, null) separately, and collapsing the two would lose the " +
                "distinction it acts on");
        }

        // =================================================================== L-4

        [Test]
        public void CacheBudget_Tracks_One_Total_Against_One_Ceiling()
        {
            var config = NewCacheConfig(maxBytes: 1000);
            var budget = new CacheBudget(config);

            Assert.AreEqual(1000L, budget.Max);
            Assert.AreEqual(0L, budget.Current);

            Assert.IsTrue(budget.TryAdmit(600), "600 of 1000 is still within the ceiling");
            Assert.IsFalse(budget.TryAdmit(600),
                "1200 of 1000 is not — TryAdmit always admits, and reports whether the caller now " +
                "owes an eviction pass");
            Assert.AreEqual(1200L, budget.Current, "the bytes are admitted either way");

            budget.Give(1300);
            Assert.AreEqual(0L, budget.Current,
                "giving back more than was taken clamps at zero rather than going negative — a " +
                "negative total would read as free space and disable eviction outright");

            config.MaxCacheSizeBytes = 2000;
            Assert.AreEqual(2000L, budget.Max,
                "the ceiling is read live from the config, not snapshotted, because the field is " +
                "public and mutable and every call site read it live before this type existed");

            var unlimited = new CacheBudget(NewCacheConfig(maxBytes: 0));
            Assert.IsTrue(unlimited.TryAdmit(long.MaxValue / 2), "0 means unlimited");
            Assert.AreEqual(0f, unlimited.UsageRatio, "and an unlimited budget has no usage ratio");
        }

        [Test]
        public void One_Shared_Budget_Caps_Several_Per_Type_Caches_At_One_Ceiling()
        {
            // L-4. Each per-type TieredCache<T> used to hold its own byte total and gate eviction
            // against the same MaxCacheSizeBytes in isolation, so N types meant N x the configured
            // ceiling with every individual cache honestly reporting itself nearly empty.
            var config = NewCacheConfig(maxBytes: 1000);
            var shared = new CacheBudget(config);

            var cacheA = Track(new TieredCache<BudgetAssetA>(config, shared));
            var cacheB = Track(new TieredCache<BudgetAssetB>(config, shared));

            var handleA = new FakeHandle<BudgetAssetA>(new BudgetAssetA());
            var handleB = new FakeHandle<BudgetAssetB>(new BudgetAssetB());

            cacheA.Set("a/one", handleA, 400);
            cacheB.Set("b/one", handleB, 400);

            Assert.AreEqual(2, handleA.ReferenceCount,
                "sanity: the cache took its own reference (the Wave 1 contract), so the budget " +
                "figures below come from a genuinely admitted entry rather than a rejected one");

            Assert.AreEqual(800L, shared.Current,
                "L-4: the two caches account into one total");
            Assert.AreEqual(1000L, shared.Max,
                "against one ceiling — not 1000 per type");
            Assert.AreEqual(0.8f, shared.UsageRatio, 0.0001f,
                "so 400 + 400 already reads as 80% full, which is the number the eviction gate reads");

            var statsA = cacheA.GetStatistics();
            var statsB = cacheB.GetStatistics();

            Assert.AreEqual(400L, statsA.TotalSizeBytes);
            Assert.AreEqual(400L, statsB.TotalSizeBytes);
            Assert.AreEqual(statsA.TotalSizeBytes + statsB.TotalSizeBytes, shared.Current,
                "the per-cache figures are the reporting half and must still sum to the enforcement " +
                "half, or the two books have drifted");

            Assert.AreEqual(1000L, statsA.MaxSizeBytes);
            Assert.AreEqual(1000L, statsB.MaxSizeBytes,
                "and both caches report the shared ceiling, so neither can believe it owns 1000 of " +
                "its own on top of the other's");

            // The overflow a shared budget can see and two private ones cannot.
            cacheA.Set("a/two", new FakeHandle<BudgetAssetA>(new BudgetAssetA()), 400);

            Assert.AreEqual(1200L, shared.Current);
            Assert.Greater(shared.UsageRatio, 1f,
                "1200 bytes against a 1000-byte ceiling is visible as over-budget from either " +
                "cache, which is the whole point of sharing the total");
        }

        [Test]
        public void Private_Budgets_Let_Two_Caches_Hold_Twice_The_Configured_Ceiling()
        {
            // The counterfactual that makes the test above mean something: the same two caches,
            // each on its own budget (the public constructor), holding 1600 bytes between them
            // against a 1000-byte ceiling while each one reports itself at 80% and neither ever
            // triggers an eviction pass. This is the L-4 defect, reproduced.
            var config = NewCacheConfig(maxBytes: 1000);

            var cacheA = Track(new TieredCache<BudgetAssetA>(config));
            var cacheB = Track(new TieredCache<BudgetAssetB>(config));

            cacheA.Set("a/one", new FakeHandle<BudgetAssetA>(new BudgetAssetA()), 800);
            cacheB.Set("b/one", new FakeHandle<BudgetAssetB>(new BudgetAssetB()), 800);

            var statsA = cacheA.GetStatistics();
            var statsB = cacheB.GetStatistics();

            Assert.AreEqual(0.8f, statsA.UsageRatio, 0.0001f);
            Assert.AreEqual(0.8f, statsB.UsageRatio, 0.0001f);
            Assert.AreEqual(1600L, statsA.TotalSizeBytes + statsB.TotalSizeBytes,
                "1600 bytes are resident against a ceiling of 1000, and no single cache's own " +
                "figures can reveal it — this is why the budget has to be shared to be a budget");
        }

        /// <summary>
        /// Eviction is switched off in these fixtures on purpose: L-4 is about which total the
        /// ceiling is measured against, and an eviction pass firing mid-assertion would move the
        /// very numbers under test. Everything else is left at the shipped defaults.
        /// </summary>
        private static TieredCacheConfig NewCacheConfig(long maxBytes)
        {
            var config = TieredCacheConfig.Default;
            config.MaxCacheSizeBytes = maxBytes;
            config.EnableAutoEviction = false;
            config.EnableAutoTiering = false;
            return config;
        }

        /// <summary>
        /// A minimal <see cref="IAssetHandle{T}"/> with a working reference count, so cache
        /// accounting can be tested without Addressables content.
        /// </summary>
        /// <remarks>
        /// Implements the internal <see cref="IRetainableHandle"/> as well, so <c>TryRetain()</c>
        /// takes the same atomic path every in-package handle takes rather than the extension's
        /// check-then-retain fallback for foreign implementations. Deliberately not an
        /// <c>IOwnedHandle</c>: nothing here tests teardown, and a fake that answers more of the
        /// contract than it needs to invites a test to lean on the fake instead of the code.
        /// </remarks>
        private sealed class FakeHandle<T> : IAssetHandle<T>, IRetainableHandle where T : class
        {
            private int _references = 1;

            public FakeHandle(T asset)
            {
                Asset = asset;
            }

            public T Asset { get; }
            public bool IsValid => _references > 0;
            public AsyncOperationStatus Status => AsyncOperationStatus.Succeeded;
            public float Progress => 1f;
            public int ReferenceCount => _references;

            public void Retain()
            {
                if (_references <= 0) throw new ObjectDisposedException(nameof(FakeHandle<T>));
                _references++;
            }

            public bool TryRetain()
            {
                if (_references <= 0) return false;
                _references++;
                return true;
            }

            public void Release()
            {
                if (_references > 0) _references--;
            }

            public AsyncOperationHandle<T> GetHandle() => default;

            public void Dispose() => Release();
        }
    }
}
