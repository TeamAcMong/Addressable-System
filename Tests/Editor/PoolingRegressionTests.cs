using System;
using AddressableManager.Pooling;
using AddressableManager.Pooling.Adapters;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace AddressableManager.Tests
{
    /// <summary>
    /// Regression guard for <c>Runtime/Pooling</c> (discovery report P-35). Before this file the
    /// whole pooling subsystem had zero test coverage: P-1..P-7 shipped without one, and
    /// HANDOFF_TO_SESSION_B.md §8.2 names three <c>UnityEngine.Pool.ObjectPool</c> behaviours that
    /// P-3/P-4/P-7 and P-12/P-14/P-26 all depend on and that nobody had ever verified from this repo.
    /// </summary>
    /// <remarks>
    /// Everything here is a pure EditMode unit test over the pool adapters, <see cref="DynamicPool{T}"/>
    /// and <see cref="DynamicPoolConfig"/>, using a plain POCO as the pooled type. It deliberately
    /// does NOT exercise <c>AddressablePoolManager</c>: that needs a live <c>AssetLoader</c> and real
    /// Addressables content, which belongs in the PlayMode integration suite rather than here.
    ///
    /// <see cref="LogAssert.ignoreFailingMessages"/> is on for the whole fixture because several of
    /// the behaviours under test are *defined* as "logs an error and returns a neutral value"
    /// (P-15), and the assertions below check the returned values rather than the log text.
    /// </remarks>
    [TestFixture]
    public class PoolingRegressionTests
    {
        private sealed class Item
        {
            public int Id;
        }

        private bool _previousIgnoreFailingMessages;

        [SetUp]
        public void SetUp()
        {
            _previousIgnoreFailingMessages = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = _previousIgnoreFailingMessages;
        }

        private static Func<Item> Counter(int[] created)
        {
            return () =>
            {
                created[0]++;
                return new Item { Id = created[0] };
            };
        }

        // ------------------------------------------------------------------ §8.2: the three
        // UnityEngine.Pool.ObjectPool behaviours this package's adapters are built on top of and
        // that no test in this repo had ever pinned down.

        [Test]
        public void UnityObjectPool_Constructor_RejectsNonPositiveMaxSize()
        {
            // UnityPoolAdapter translates maxSize <= 0 to int.MaxValue ("unlimited", P-7) precisely
            // because passing it through would throw here.
            Assert.Throws<ArgumentException>(
                () => new UnityEngine.Pool.ObjectPool<Item>(() => new Item(), maxSize: 0));
            Assert.Throws<ArgumentException>(
                () => new UnityEngine.Pool.ObjectPool<Item>(() => new Item(), maxSize: -1));
        }

        [Test]
        public void UnityObjectPool_CountAll_IncrementsOnlyWhenGetConstructs()
        {
            var pool = new UnityEngine.Pool.ObjectPool<Item>(() => new Item(), maxSize: 10);

            Assert.AreEqual(0, pool.CountAll);

            var a = pool.Get();
            Assert.AreEqual(1, pool.CountAll, "Get() that constructs must increment CountAll");

            pool.Release(a);
            Assert.AreEqual(1, pool.CountAll, "Release() must not change CountAll");

            pool.Get();
            Assert.AreEqual(1, pool.CountAll, "Get() that reuses must not change CountAll");

            // The consequence UnityPoolAdapter.Clear() (P-14) leans on: a Release with no matching
            // Get drives CountActive negative, because only Get can raise CountAll.
            var fresh = new UnityEngine.Pool.ObjectPool<Item>(() => new Item(), maxSize: 10);
            fresh.Release(new Item());
            Assert.AreEqual(0, fresh.CountAll);
            Assert.AreEqual(1, fresh.CountInactive);
            Assert.AreEqual(-1, fresh.CountActive,
                "CountActive is CountAll - CountInactive and is allowed to go negative");
        }

        [Test]
        public void UnityObjectPool_CollectionCheck_ThrowsOnlyForAnAlreadyInactiveInstance()
        {
            var pool = new UnityEngine.Pool.ObjectPool<Item>(() => new Item(), collectionCheck: true);

            var borrowed = pool.Get();
            pool.Release(borrowed);

            Assert.Throws<InvalidOperationException>(() => pool.Release(borrowed),
                "double-release of an instance already on the free list must throw");

            // ...but a stranger is silently adopted, which is exactly why P-7 had to verify
            // ownership in AddressablePoolManager.Despawn instead of relying on the adapter.
            Assert.DoesNotThrow(() => pool.Release(new Item()),
                "collectionCheck does not know which instances the pool handed out");
        }

        // ------------------------------------------------------------------ P-11 / P-12 / P-27:
        // Prewarm must mean the same thing on both adapters.

        [Test]
        public void Prewarm_NeverInvokesOnGet_OnEitherAdapter()
        {
            int unityOnGet = 0;
            var unity = new UnityPoolAdapter<Item>(() => new Item(), onGet: _ => unityOnGet++, maxSize: 100);
            unity.Prewarm(5);
            Assert.AreEqual(0, unityOnGet,
                "P-11: preloading must not run the caller's onGet — that callback is where the " +
                "manager does SetActive(true), i.e. a full OnEnable/OnDisable cycle per instance");
            Assert.AreEqual((0, 5), unity.GetStats());

            int customOnGet = 0;
            var custom = new CustomPoolAdapter<Item>(() => new Item(), onGet: _ => customOnGet++, maxSize: 100);
            custom.Prewarm(5);
            Assert.AreEqual(0, customOnGet);
            Assert.AreEqual((0, 5), custom.GetStats());
        }

        [Test]
        public void Prewarm_StopsAtMaxSize_OnEitherAdapter()
        {
            var unityCreated = new[] { 0 };
            var unity = new UnityPoolAdapter<Item>(Counter(unityCreated), maxSize: 10);
            int unityAchieved = unity.PrewarmMeasured(25);

            Assert.AreEqual(10, unityAchieved, "P-27: the achieved count, not the requested one");
            Assert.AreEqual((0, 10), unity.GetStats());
            Assert.AreEqual(10, unityCreated[0],
                "P-12: instances above maxSize must never be instantiated at all — the old code " +
                "created all 25 and let Release() destroy the 15 that overflowed");

            var customCreated = new[] { 0 };
            var custom = new CustomPoolAdapter<Item>(Counter(customCreated), maxSize: 10);
            int customAchieved = custom.PrewarmMeasured(25);

            Assert.AreEqual(10, customAchieved);
            Assert.AreEqual((0, 10), custom.GetStats());
            Assert.AreEqual(10, customCreated[0]);
        }

        [Test]
        public void Prewarm_AddsTheRequestedCount_ToAnAlreadyPopulatedPool_OnEitherAdapter()
        {
            var unity = new UnityPoolAdapter<Item>(() => new Item(), maxSize: 100);
            unity.Prewarm(3);
            Assert.AreEqual(2, unity.PrewarmMeasured(2));
            Assert.AreEqual((0, 5), unity.GetStats(),
                "Prewarm(N) means 'add N', not 'ensure N' — both adapters must agree");

            var custom = new CustomPoolAdapter<Item>(() => new Item(), maxSize: 100);
            custom.Prewarm(3);
            Assert.AreEqual(2, custom.PrewarmMeasured(2));
            Assert.AreEqual((0, 5), custom.GetStats());
        }

        // ------------------------------------------------------------------ P-4: resizing must not
        // corrupt the active/pooled split in either direction.

        [Test]
        public void TrimExcess_LeavesActiveCountAlone_OnEitherAdapter()
        {
            var unity = new UnityPoolAdapter<Item>(() => new Item(), maxSize: 100);
            unity.Prewarm(5);
            unity.Get();
            unity.Get();
            Assert.AreEqual((2, 3), unity.GetStats());

            Assert.AreEqual(2, unity.TrimExcessMeasured(2));
            Assert.AreEqual((2, 1), unity.GetStats(),
                "P-4: a trim pops from the inner pool without a matching Release, which used to " +
                "inflate CountActive by one per trimmed instance forever");

            var custom = new CustomPoolAdapter<Item>(() => new Item(), maxSize: 100);
            custom.Prewarm(5);
            custom.Get();
            custom.Get();
            Assert.AreEqual((2, 3), custom.GetStats());

            Assert.AreEqual(2, custom.TrimExcessMeasured(2));
            Assert.AreEqual((2, 1), custom.GetStats());
        }

        // ------------------------------------------------------------------ P-14: Clear() and
        // outstanding borrows.

        [Test]
        public void Clear_KeepsOutstandingBorrowsCountedAsActive_OnEitherAdapter()
        {
            var unity = new UnityPoolAdapter<Item>(() => new Item(), maxSize: 100);
            var unityBorrowed = new[] { unity.Get(), unity.Get(), unity.Get() };
            unity.Clear();

            Assert.AreEqual((3, 0), unity.GetStats(),
                "P-14: ObjectPool.Clear() zeroes CountAll; the adapter must not let three live " +
                "instances vanish from the books");

            unity.Release(unityBorrowed[0]);
            Assert.AreEqual((2, 1), unity.GetStats(),
                "and a later release of one of them must bring the count down by exactly one");

            var custom = new CustomPoolAdapter<Item>(() => new Item(), maxSize: 100);
            var customBorrowed = new[] { custom.Get(), custom.Get(), custom.Get() };
            custom.Clear();

            Assert.AreEqual((3, 0), custom.GetStats());

            custom.Release(customBorrowed[0]);
            Assert.AreEqual((2, 1), custom.GetStats());
        }

        // ------------------------------------------------------------------ P-8: a borrowed
        // instance destroyed out in the world.

        [Test]
        public void ForgetActive_RemovesTheBorrowFromActiveCount_OnEitherAdapter()
        {
            var unity = new UnityPoolAdapter<Item>(() => new Item(), maxSize: 100);
            var lostFromUnity = unity.Get();
            unity.Get();
            Assert.AreEqual((2, 0), unity.GetStats());

            Assert.IsTrue(unity.ForgetActive(lostFromUnity));
            Assert.AreEqual((1, 0), unity.GetStats(),
                "P-8: an instance destroyed outside Despawn must stop counting as active, or the " +
                "ratio feeding DynamicPool.CheckForGrowth only ever climbs");

            var custom = new CustomPoolAdapter<Item>(() => new Item(), maxSize: 100);
            var lostFromCustom = custom.Get();
            custom.Get();
            Assert.AreEqual((2, 0), custom.GetStats());

            Assert.IsTrue(custom.ForgetActive(lostFromCustom));
            Assert.AreEqual((1, 0), custom.GetStats());
            Assert.IsFalse(custom.ForgetActive(lostFromCustom),
                "an adapter with a real membership set must answer honestly the second time");
        }

        // ------------------------------------------------------------------ P-15: use after
        // dispose is uniform across all three IObjectPool<T> implementations.

        [Test]
        public void UseAfterDispose_ReturnsNeutralValues_AndNeverThrows()
        {
            // Set here, not only in [SetUp]. On this test-framework version the per-test log scope
            // is opened AFTER [SetUp] runs, and opening it resets this flag — so the assignment in
            // [SetUp] never reached the code under test and every deliberate Debug.LogError below
            // failed the test as an "Unhandled log message". Every error these calls emit is the
            // behaviour being asserted: P-15 says use-after-dispose logs and returns a neutral
            // value rather than throwing.
            LogAssert.ignoreFailingMessages = true;

            var unity = new UnityPoolAdapter<Item>(() => new Item(), maxSize: 100);
            unity.Dispose();
            Assert.IsNull(unity.Get());
            Assert.AreEqual(0, unity.PrewarmMeasured(3));
            Assert.AreEqual(0, unity.TrimExcessMeasured(3));
            Assert.AreEqual((0, 0), unity.GetStats());
            Assert.DoesNotThrow(() => unity.Clear());

            var custom = new CustomPoolAdapter<Item>(() => new Item(), maxSize: 100);
            custom.Dispose();
            Assert.IsNull(custom.Get());
            Assert.AreEqual(0, custom.PrewarmMeasured(3));
            Assert.AreEqual(0, custom.TrimExcessMeasured(3));
            Assert.AreEqual((0, 0), custom.GetStats());

            var dynamicPool = NewDynamicPool(DynamicPoolConfig.Default);
            dynamicPool.Dispose();
            Assert.IsNull(dynamicPool.Get(),
                "P-15: DynamicPool used to throw ObjectDisposedException here while both adapters " +
                "returned null through the very same IObjectPool<T> reference");
            Assert.AreEqual(0, dynamicPool.PrewarmMeasured(3));
            Assert.AreEqual(0, dynamicPool.TrimExcessMeasured(3));
            Assert.AreEqual((0, 0), dynamicPool.GetStats());
        }

        // ------------------------------------------------------------------ P-32.

        [Test]
        public void CustomPoolAdapter_DoesNotTrackANullFromCreateFunc_AsActive()
        {
            // See UseAfterDispose_ReturnsNeutralValues_AndNeverThrows for why this is set here and
            // not only in [SetUp]. The error this provokes is the documented P-32 behaviour.
            LogAssert.ignoreFailingMessages = true;

            var pool = new CustomPoolAdapter<Item>(() => null, maxSize: 100);

            Assert.IsNull(pool.Get());
            Assert.AreEqual((0, 0), pool.GetStats(),
                "P-32: a null added to _activeObjects inflates activeCount forever, because " +
                "Release(null) early-returns before it could ever be taken back out");
        }

        // ------------------------------------------------------------------ DynamicPool wiring.

        private static DynamicPool<Item> NewDynamicPool(DynamicPoolConfig config)
        {
            var inner = new CustomPoolAdapter<Item>(() => new Item(), maxSize: config.MaxSize);
            return new DynamicPool<Item>(inner, config, () => new Item(), _ => { }, "test");
        }

        [Test]
        public void DynamicPool_PrewarmMeasured_ReportsWhatTheInnerPoolAchieved()
        {
            var pool = NewDynamicPool(DynamicPoolConfig.Default);

            Assert.AreEqual(4, pool.PrewarmMeasured(4));
            Assert.AreEqual((0, 4), pool.GetStats(),
                "P-3/P-4: a preload must not register as usage");
        }

        [Test]
        public void DynamicPool_InitialCapacity_CreatesNothing()
        {
            var pool = NewDynamicPool(DynamicPoolConfig.Default);

            var stats = pool.GetDynamicStats();
            Assert.AreEqual(10, stats.CurrentCapacity);
            Assert.AreEqual(0, stats.TotalCount,
                "P-18: InitialCapacity is the auto-resize budget, not a population — this is the " +
                "documented behaviour, and the test exists so that changing it is a deliberate act");
        }

        [Test]
        public void DynamicPool_EvaluateAutoResize_IsSafeOnAnIdlePool()
        {
            var pool = NewDynamicPool(DynamicPoolConfig.Default);
            pool.PrewarmMeasured(4);

            // P-17: the clock-driven entry point exists at all. It arms the shrink on the first
            // call; nothing may be destroyed before ShrinkDelaySeconds has elapsed.
            Assert.DoesNotThrow(() => pool.EvaluateAutoResize());
            Assert.DoesNotThrow(() => pool.EvaluateAutoResize());
            Assert.AreEqual((0, 4), pool.GetStats(),
                "an armed-but-not-yet-elapsed shrink must not destroy anything");
            Assert.IsTrue(pool.GetDynamicStats().IsShrinkPending,
                "P-17: the shrink state machine must be able to advance without a Release() — it " +
                "used to be reachable only from Release, so an idle pool never shrank");
        }

        // ------------------------------------------------------------------ P-19 / P-20.

        [Test]
        public void DynamicPoolConfig_Validate_RejectsZeroMaxSize()
        {
            var config = DynamicPoolConfig.Default;
            config.MinSize = 0;
            config.InitialCapacity = 0;
            config.MaxSize = 0;

            Assert.IsFalse(config.Validate(out var error),
                "P-19: MaxSize 0 is a hard cap of zero here but means 'unlimited' to " +
                "IPoolFactory.CreatePool; accepting it produced a pool that could never grow on top " +
                "of a free list that was never bounded");
            StringAssert.Contains("MaxSize", error);
        }

        [Test]
        public void DynamicPoolConfig_Fixed_Zero_FailsValidation()
        {
            Assert.IsFalse(DynamicPoolConfig.Fixed(0).Validate(out _));
            Assert.IsTrue(DynamicPoolConfig.Fixed(1).Validate(out _));
            Assert.IsTrue(DynamicPoolConfig.Fixed(20).Validate(out _));
        }

        [Test]
        public void DynamicPoolConfig_Validate_RejectsZeroShrinkFactor()
        {
            var config = DynamicPoolConfig.Default;
            config.ShrinkFactor = 0f;

            Assert.IsFalse(config.Validate(out var error),
                "P-20: 'never shrink' reads as expressible with ShrinkFactor 0 and is not — the " +
                "shrink-by-at-least-1 floor still applies");
            StringAssert.Contains("EnableAutoResize", error);
        }

        [Test]
        public void DynamicPoolConfig_Presets_AllValidate()
        {
            Assert.IsTrue(DynamicPoolConfig.Default.Validate(out _));
            Assert.IsTrue(DynamicPoolConfig.Conservative.Validate(out _));
            Assert.IsTrue(DynamicPoolConfig.Aggressive.Validate(out _));
        }
    }
}
