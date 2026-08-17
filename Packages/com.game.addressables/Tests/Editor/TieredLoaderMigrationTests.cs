using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AddressableManager.API;
using AddressableManager.Core;
using AddressableManager.Loaders;
using NUnit.Framework;
using UnityEngine;

// TieredAssetLoader IS the subject of this fixture, so CS0618 is suppressed for the whole file
// rather than at each call site. A test that cannot compile against its own subject is useless,
// and a per-call-site pragma would be ~20 pairs of lines saying the same thing.
#pragma warning disable 618

namespace AddressableManager.Tests.Editor
{
    /// <summary>
    /// The L-7 migration: <c>TieredAssetLoader</c> retired as a fork, tiering became a
    /// configuration of <see cref="AssetLoader"/>, and the deprecated class became a forwarder.
    /// </summary>
    /// <remarks>
    /// TWO THINGS ARE BEING GUARDED, AND THE SECOND IS THE POINT.
    ///
    /// The first is compatibility: the shim must still expose every public signature the fork had,
    /// because repo invariant 6 keeps 4.x source compiling until 5.0.0. That is a surface check and
    /// it is done by reflection over the whole declared surface, so a member quietly dropped or
    /// renamed fails here rather than in a consumer's project.
    ///
    /// The second is the bug that drove the decision. A <c>TieredAssetLoader</c> was not an
    /// <see cref="AssetLoader"/> and registered with a registry of its own, so
    /// <c>AssetLoaderRegistry.InvalidateAll</c> — what <c>CatalogService</c> calls after a CDN
    /// catalog update — could never reach it, and its cache went on serving handles resolved
    /// against the previous catalog for the rest of the session with no code path that could fix
    /// it. Reachability is therefore asserted <em>directly</em>, against the actual list the
    /// invalidation walk enumerates, not inferred from a count that a foreign loader appearing or
    /// being collected mid-test could move. If someone reintroduces a separate cache or a second
    /// registry, <see cref="There_Is_Exactly_One_Loader_Registry"/> and
    /// <see cref="A_TieredAssetLoader_Is_Reached_By_The_Catalog_Walk_Through_Its_Inner_Loader"/>
    /// are the two that fail.
    ///
    /// Deliberately no load here. Every assertion below is reachable without Addressables content,
    /// which is what keeps this an EditMode fixture; the load paths belong in PlayMode.
    /// </remarks>
    [TestFixture]
    public class TieredLoaderMigrationTests
    {
        private readonly List<AssetLoader> _loaders = new List<AssetLoader>();
        private readonly List<TieredAssetLoader> _shims = new List<TieredAssetLoader>();

        private AssetLoader NewLoader(string scope)
        {
            var loader = new AssetLoader(scope);
            _loaders.Add(loader);
            return loader;
        }

        private AssetLoader NewLoader(string scope, TieredCacheConfig tiering)
        {
            var loader = new AssetLoader(scope, tiering);
            _loaders.Add(loader);
            return loader;
        }

        private TieredAssetLoader NewShim(string scope = "Unknown", TieredCacheConfig config = null)
        {
            var shim = new TieredAssetLoader(scope, config);
            _shims.Add(shim);
            return shim;
        }

        [TearDown]
        public void DisposeCreated()
        {
            foreach (var shim in _shims)
            {
                try { shim.Dispose(); } catch { /* a test may have disposed it already */ }
            }

            foreach (var loader in _loaders)
            {
                try { loader.Dispose(); } catch { /* a test may have disposed it already */ }
            }

            _shims.Clear();
            _loaders.Clear();
        }

        // ------------------------------------------------------------------ reachability: the
        // regression that would silently return if someone reintroduced a separate cache.

        /// <summary>
        /// The exact array <c>InvalidateAll</c>/<c>PumpAll</c>/<c>ForceEvictionAll</c> walk.
        /// </summary>
        /// <remarks>
        /// Reflected rather than inferred from <c>LiveCount</c> on purpose. A count says "one more
        /// loader is registered than before", which a loader created by another fixture — or one
        /// collected between two reads of a weak-reference list — can make true or false for
        /// reasons that have nothing to do with the subject. Membership of the snapshot says "this
        /// object is one of the ones the walk will hand to <c>InvalidateAddresses</c>", which is the
        /// claim under test.
        /// </remarks>
        private static AssetLoader[] RegistrySnapshot()
        {
            var snapshot = typeof(AssetLoaderRegistry)
                .GetMethod("Snapshot", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(snapshot,
                "AssetLoaderRegistry.Snapshot() is the one walk every registry duty goes through. " +
                "If it was renamed, update this helper — do not delete the test, the reachability " +
                "claim it guards is the whole reason the fork was retired.");

            return (AssetLoader[])snapshot.Invoke(null, null);
        }

        /// <summary>
        /// The <see cref="AssetLoader"/> a shim forwards to. Private field, reached by reflection,
        /// because "the inner loader is registered" is precisely the mechanism under test and there
        /// is no public way to observe it.
        /// </summary>
        private static AssetLoader InnerLoaderOf(TieredAssetLoader shim)
        {
            var field = typeof(TieredAssetLoader)
                .GetField("_inner", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.IsNotNull(field,
                "TieredAssetLoader._inner is the composition that makes the shim reachable from a " +
                "catalog update. If it was renamed, update this helper.");

            var inner = (AssetLoader)field.GetValue(shim);
            Assert.IsNotNull(inner, "The shim must construct its inner loader in its constructor.");
            return inner;
        }

        [Test]
        public void A_Tiering_Configured_Loader_Is_Reached_By_The_Catalog_Walk()
        {
            var tiered = NewLoader("l7-tiered", TieredCacheConfig.Default);

            Assert.IsTrue(tiered.TieringEnabled,
                "The two-argument constructor is what turns tiering on; nothing else does.");

            CollectionAssert.Contains(RegistrySnapshot(), tiered,
                "A tiered loader registers through AssetLoader's own constructor like every other " +
                "loader. This is the whole payoff of the merge: tiering no longer costs a loader " +
                "its reachability from a CDN catalog update.");
        }

        [Test]
        public void A_TieredAssetLoader_Is_Reached_By_The_Catalog_Walk_Through_Its_Inner_Loader()
        {
            var shim = NewShim("l7-shim", TieredCacheConfig.Aggressive);
            var inner = InnerLoaderOf(shim);

            Assert.IsFalse(typeof(AssetLoader).IsAssignableFrom(typeof(TieredAssetLoader)),
                "The shim is still not an AssetLoader — so if this passes, reachability came from " +
                "composition and not from an inheritance change that would have made the test " +
                "trivially true.");

            CollectionAssert.Contains(RegistrySnapshot(), inner,
                "THE REGRESSION. Before L-7 a TieredAssetLoader was invisible to " +
                "AssetLoaderRegistry.InvalidateAll, so after a catalog update its cache kept " +
                "serving handles resolved against the previous catalog, permanently, with no code " +
                "path that could reach it. If this fails, a separate cache has been reintroduced.");
        }

        [Test]
        public void InvalidateAll_Counts_A_TieredAssetLoader_Among_The_Loaders_It_Reached()
        {
            int before = AssetLoaderRegistry.LiveCount;

            var shim = NewShim("l7-count", TieredCacheConfig.Default);

            Assert.AreEqual(before + 1, AssetLoaderRegistry.LiveCount,
                "Constructing a shim registers exactly one loader — its inner one — and no more. " +
                "Two would mean a second registration path had been added alongside the ctor's.");

            int reached = AssetLoaderRegistry.InvalidateAll(new[] { "some/address" });

            Assert.AreEqual(AssetLoaderRegistry.LiveCount, reached,
                "The walk must reach every live loader, not a subset.");
            Assert.GreaterOrEqual(reached, before + 1,
                "...and the shim just constructed is one of them.");

            GC.KeepAlive(shim);
        }

        [Test]
        public void There_Is_Exactly_One_Loader_Registry()
        {
            var duplicates = typeof(AssetLoader).Assembly
                .GetTypes()
                .Where(t => t.Name.IndexOf("LoaderRegistry", StringComparison.Ordinal) >= 0)
                .ToArray();

            CollectionAssert.AreEquivalent(new[] { typeof(AssetLoaderRegistry) }, duplicates,
                "TieredAssetLoaderRegistry was deleted because a second registry has to be kept in " +
                "step with the first by hand, and the catalog-update hole existed for exactly as " +
                "long as it was not. One population, one list.");
        }

        [Test]
        public void Disposing_The_Shim_Unregisters_The_Inner_Loader_And_Is_Idempotent()
        {
            var shim = NewShim("l7-dispose", TieredCacheConfig.Default);
            var inner = InnerLoaderOf(shim);

            int withIt = AssetLoaderRegistry.LiveCount;

            shim.Dispose();

            Assert.AreEqual(withIt - 1, AssetLoaderRegistry.LiveCount);
            CollectionAssert.DoesNotContain(RegistrySnapshot(), inner,
                "A disposed loader must leave the walk; a catalog update touching it would mutate " +
                "collections teardown has already emptied.");

            Assert.DoesNotThrow(() => shim.Dispose(),
                "The shim keeps no _disposed flag of its own — idempotence lives in " +
                "AssetLoader.Dispose, which is reached twice here.");
            Assert.AreEqual(withIt - 1, AssetLoaderRegistry.LiveCount,
                "The second Dispose must not unregister a second loader.");
        }

        // ------------------------------------------------------------------ the shim still has
        // every public signature the fork had.

        [Test]
        public void The_Shim_Is_Obsolete_As_A_Warning_And_Names_Its_Replacement()
        {
            var obsolete = typeof(TieredAssetLoader).GetCustomAttribute<ObsoleteAttribute>(inherit: false);

            Assert.IsNotNull(obsolete,
                "Invariant 6: the class is retired, not deleted, and the retirement has to be " +
                "visible at the call site.");
            Assert.IsFalse(obsolete.IsError,
                "A warning, not an error — 4.x code must keep compiling until 5.0.0.");

            StringAssert.Contains("AssetLoader", obsolete.Message,
                "The message has to name the replacement, not just complain.");
            StringAssert.Contains("5.0.0", obsolete.Message,
                "...and say when the escape hatch closes.");
        }

        [Test]
        public void The_Shim_Kept_Every_Public_Member_The_Fork_Had()
        {
            // EXACTLY the surface of the pre-merge class (git HEAD:TieredAssetLoader.cs), with no
            // deviation at all — including LoadAssetAsync, which returns Task in every project,
            // UniTask installed or not.
            //
            // This table briefly expected UniTask under UNITASK_PRESENT, on the reasoning that
            // invariant 3 (dual signatures) applies package-wide. It does not apply here, and the
            // repo had already settled the identical question the other way one file over: see
            // Standard.LoadScene<T>, kept at Task while its replacements are dual, "a deprecated
            // method changing its return type would break the very callers the deprecation exists to
            // keep compiling until 5.0.0". A warning-level [Obsolete] promises source compatibility
            // until 5.0.0; a return type that changes with an unrelated package's presence breaks
            // `Task<T> t = loader.LoadAssetAsync<Sprite>(a);` and every Task.WhenAll call site with
            // CS0029/CS1503, and drowns the deprecation warning under a hard error on the same line.
            // Where invariants 3 and 6 collide on an [Obsolete] member, 6 wins.
            //
            // The UniTask allocation that motivated the change is real, and it is the reason to
            // migrate rather than a reason to break the signature: the replacement,
            // Advanced.CreateLoader(name, cfg), returns an AssetLoader whose LoadAssetAsync IS dual
            // and needs no conversion. The shim pays one AsTask() and keeps compiling.
            var expected = new[]
            {
                "Task<IAssetHandle<T>> LoadAssetAsync<T>(String)",
                "Task<IAssetHandle<T>> LoadAssetAsync<T>(AssetReference)",
                "Void PinAsset<T>(String)",
                "Void UnpinAsset<T>(String)",
                "Nullable<TieredCacheStats> GetCacheStats<T>()",
                "TieredCacheStats GetCombinedStats()",
                "Void EvaluateTiers()",
                "Void ForceEviction()",
                "Void ClearCache()",
                "Void Dispose()"
            };

            var actual = typeof(TieredAssetLoader)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName)
                .Select(Describe)
                .ToArray();

            CollectionAssert.AreEquivalent(expected, actual,
                "A member missing from the right-hand side is 4.x source that stops compiling " +
                "before 5.0.0. A member missing from the left is one added to a class that is " +
                "supposed to be frozen — decide deliberately and update the table.");
        }

        [Test]
        public void The_Shim_Still_Implements_IDisposable_And_Keeps_Its_Constructor_Defaults()
        {
            Assert.IsTrue(typeof(IDisposable).IsAssignableFrom(typeof(TieredAssetLoader)),
                "`using (var loader = new TieredAssetLoader(...))` is 4.x source.");

            var ctor = typeof(TieredAssetLoader)
                .GetConstructor(new[] { typeof(string), typeof(TieredCacheConfig) });

            Assert.IsNotNull(ctor, "The fork's only constructor was (string, TieredCacheConfig).");

            var parameters = ctor.GetParameters();
            Assert.IsTrue(parameters[0].HasDefaultValue);
            Assert.AreEqual("Unknown", parameters[0].DefaultValue,
                "`new TieredAssetLoader()` compiled before and must still compile.");
            Assert.IsTrue(parameters[1].HasDefaultValue);
            Assert.IsNull(parameters[1].DefaultValue,
                "null still means TieredCacheConfig.Default — see the next test for the consequence.");
        }

        [Test]
        public void The_Shims_Generic_Methods_Kept_Their_Reference_Type_Constraint()
        {
            // The fork declared `where T : class` on all four. Tightening a constraint breaks
            // source; the merged AssetLoader has no constraint at all, so a forwarder written
            // straight through would have dropped it. Either is a change worth noticing.
            foreach (var name in new[] { "LoadAssetAsync", "PinAsset", "UnpinAsset", "GetCacheStats" })
            {
                var methods = typeof(TieredAssetLoader)
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == name && m.IsGenericMethodDefinition)
                    .ToArray();

                CollectionAssert.IsNotEmpty(methods, $"{name} must still be generic.");

                foreach (var method in methods)
                {
                    var argument = method.GetGenericArguments()[0];
                    Assert.IsTrue(
                        argument.GenericParameterAttributes
                            .HasFlag(GenericParameterAttributes.ReferenceTypeConstraint),
                        $"{name}<T> lost `where T : class`, which the fork declared.");
                }
            }
        }

        // ------------------------------------------------------------------ the shim really
        // forwards: its state lives on the inner loader, not on a second book of its own.

        [Test]
        public void A_Default_Constructed_Shim_Is_Still_Tiered_So_GetCacheStats_Never_Returns_Null()
        {
            var shim = NewShim();

            var stats = shim.GetCacheStats<Texture2D>();

            Assert.IsTrue(stats.HasValue,
                "The null -> TieredCacheConfig.Default fallback is preserved, so tiering through " +
                "this wrapper is never off, so the inner loader's `null iff tiering is off` rule " +
                "can never fire here. Code that read null as 'nothing loaded yet' must now test " +
                "TotalEntries == 0 — which is the documented semantic shift.");
            Assert.AreEqual(0, stats.Value.TotalEntries);
        }

        [Test]
        public void A_Pin_Through_The_Shim_Lands_On_The_Inner_Loaders_Tiering_State()
        {
            var shim = NewShim("l7-forward", TieredCacheConfig.Default);
            var inner = InnerLoaderOf(shim);

            shim.PinAsset<Texture2D>("Boss/Diffuse");

            Assert.AreEqual(1, inner.GetTieredCacheStats().PendingPins,
                "Forwarding is real: the shim holds no tiering state of its own, so the pin has to " +
                "be observable on the inner loader or it went nowhere.");
            Assert.AreEqual(1, shim.GetCombinedStats().PendingPins,
                "GetCombinedStats() -> inner.GetTieredCacheStats(): the same number by both routes.");

            shim.UnpinAsset<Texture2D>("Boss/Diffuse");

            Assert.AreEqual(0, inner.GetTieredCacheStats().PendingPins);
        }

        [Test]
        public void ClearCache_Through_The_Shim_Drops_The_Inner_Loaders_Tiering_State()
        {
            var shim = NewShim("l7-clear", TieredCacheConfig.Default);

            shim.PinAsset<Texture2D>("Boss/Diffuse");
            Assert.AreEqual(1, shim.GetCombinedStats().PendingPins);

            shim.ClearCache();

            Assert.AreEqual(0, shim.GetCombinedStats().PendingPins,
                "Emptying the cache drops armed pins with it; leaving them would re-pin whatever " +
                "loaded next under those keys, long after the caller believed the cache was gone.");
        }

        [Test]
        public void EvaluateTiers_And_ForceEviction_Forward_Without_Throwing_On_An_Empty_Loader()
        {
            var shim = NewShim("l7-pump", TieredCacheConfig.Default);

            // The facade's periodic pump reaches every registered loader, most of them empty most
            // of the time. Throwing here would be logged and swallowed by AssetLoaderRegistry.PumpAll
            // once every five seconds forever.
            Assert.DoesNotThrow(() => shim.EvaluateTiers());
            Assert.DoesNotThrow(() => shim.ForceEviction());
        }

        // ------------------------------------------------------------------ the migration target
        // named in the deprecation message has to be real.

        [Test]
        public void Everything_The_Obsolete_Message_Tells_Callers_To_Use_Actually_Exists()
        {
            Assert.IsNotNull(
                typeof(AssetLoader).GetConstructor(new[] { typeof(string), typeof(TieredCacheConfig) }),
                "`new AssetLoader(\"Battle\", TieredCacheConfig.Aggressive)` is the first line of " +
                "the migration snippet in the [Obsolete] message.");

            var statsOverloads = typeof(AssetLoader)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "GetTieredCacheStats")
                .ToArray();

            Assert.AreEqual(1, statsOverloads.Count(m =>
                    !m.IsGenericMethodDefinition && m.ReturnType == typeof(TieredCacheStats)),
                "GetCombinedStats() -> GetTieredCacheStats(), one of the two renames the message names.");
            Assert.AreEqual(1, statsOverloads.Count(m =>
                    m.IsGenericMethodDefinition && m.ReturnType == typeof(TieredCacheStats?)),
                "GetCacheStats<T>() -> GetTieredCacheStats<T>(), the other one.");

            foreach (var name in new[] { "PinAsset", "UnpinAsset" })
            {
                Assert.IsTrue(
                    typeof(AssetLoader).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Any(m => m.Name == name && m.IsGenericMethodDefinition),
                    $"The message says {name}<T> is a straight type-name substitution.");
            }

            var createLoader = typeof(Advanced)
                .GetMethod("CreateLoader", new[] { typeof(string), typeof(TieredCacheConfig) });

            Assert.IsNotNull(createLoader,
                "Factory form: Advanced.CreateTieredLoader(name, cfg) -> Advanced.CreateLoader(name, cfg).");
            Assert.IsNull(createLoader.GetCustomAttribute<ObsoleteAttribute>(),
                "A deprecation that points at another deprecated member is not a migration path.");

            var createTiered = typeof(Advanced).GetMethod("CreateTieredLoader");
            Assert.IsNotNull(createTiered);
            Assert.IsNotNull(createTiered.GetCustomAttribute<ObsoleteAttribute>(),
                "Every member whose signature names TieredAssetLoader must itself carry [Obsolete], " +
                "or a caller can keep reaching the retired type without a single warning.");
        }

        [Test]
        public void An_Untiered_AssetLoader_Is_Still_The_Default_Shape()
        {
            var plain = NewLoader("l7-plain");

            Assert.IsFalse(plain.TieringEnabled,
                "Tiering is OFF by default. Every construction site that existed before the merge " +
                "uses this constructor, so nothing that ships today changed behaviour.");
            Assert.IsNull(plain.GetTieredCacheStats<Texture2D>(),
                "null from the per-type overload means exactly one thing — tiering is off.");
            CollectionAssert.Contains(RegistrySnapshot(), plain,
                "...and it is still reachable from a catalog update, which never depended on tiering.");
        }

        // ------------------------------------------------------------------ helpers

        private static string Describe(MethodInfo method)
        {
            string generics = method.IsGenericMethodDefinition
                ? "<" + string.Join(",", method.GetGenericArguments().Select(a => a.Name)) + ">"
                : string.Empty;

            string arguments = string.Join(", ",
                method.GetParameters().Select(p => Pretty(p.ParameterType)));

            return $"{Pretty(method.ReturnType)} {method.Name}{generics}({arguments})";
        }

        private static string Pretty(Type type)
        {
            if (!type.IsGenericType) return type.Name;

            int tick = type.Name.IndexOf('`');
            string bare = tick >= 0 ? type.Name.Substring(0, tick) : type.Name;

            return bare + "<" + string.Join(",", type.GetGenericArguments().Select(Pretty)) + ">";
        }
    }
}

#pragma warning restore 618
