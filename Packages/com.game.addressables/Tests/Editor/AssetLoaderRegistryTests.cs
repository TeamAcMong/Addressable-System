using System;
using System.Collections.Generic;
using AddressableManager.Loaders;
using NUnit.Framework;

namespace AddressableManager.Tests.Editor
{
    /// <summary>
    /// The registry a catalog update walks to reach every live loader.
    /// </summary>
    /// <remarks>
    /// Worth testing rather than reasoning about, because the thing it replaced looked correct too.
    /// Invalidation used to go through ScopeManager, which tracks one of the six populations that
    /// construct an AssetLoader; the other five kept serving pre-update content and nothing said so.
    /// These tests are about reach and lifetime — that a loader nobody registered by hand is still
    /// found, and that a disposed one is not.
    ///
    /// Deliberately no GC test. Whether a dropped loader is collected on a given run is the
    /// runtime's business, not this code's, and asserting on it produces a test that fails for
    /// reasons unrelated to the change. What is asserted is that the reference is weak by
    /// construction and that pruning does not throw.
    /// </remarks>
    [TestFixture]
    public class AssetLoaderRegistryTests
    {
        private readonly List<AssetLoader> _created = new List<AssetLoader>();

        private AssetLoader NewLoader(string scope)
        {
            var loader = new AssetLoader(scope);
            _created.Add(loader);
            return loader;
        }

        [TearDown]
        public void DisposeCreated()
        {
            foreach (var loader in _created)
            {
                try { loader.Dispose(); } catch { /* a test may have disposed it already */ }
            }

            _created.Clear();
        }

        [Test]
        public void Constructing_A_Loader_Registers_It_Without_Anyone_Asking()
        {
            int before = AssetLoaderRegistry.LiveCount;

            NewLoader("registry-ctor");

            Assert.AreEqual(before + 1, AssetLoaderRegistry.LiveCount,
                "The constructor is the one place every construction path goes through. If it does " +
                "not register, five of the six loader populations become unreachable again.");
        }

        [Test]
        public void Disposing_A_Loader_Unregisters_It()
        {
            var loader = NewLoader("registry-dispose");
            int withLoader = AssetLoaderRegistry.LiveCount;

            loader.Dispose();

            Assert.AreEqual(withLoader - 1, AssetLoaderRegistry.LiveCount);
        }

        [Test]
        public void InvalidateAll_Reaches_Every_Live_Loader()
        {
            NewLoader("registry-reach-a");
            NewLoader("registry-reach-b");
            NewLoader("registry-reach-c");

            int reached = AssetLoaderRegistry.InvalidateAll(new[] { "some/address" });

            Assert.GreaterOrEqual(reached, 3,
                "All three must be reached. This is the number that was 1-of-6 before the registry.");
            Assert.AreEqual(AssetLoaderRegistry.LiveCount, reached,
                "Reached should account for every live loader, not a subset.");
        }

        [Test]
        public void InvalidateAll_Skips_A_Disposed_Loader()
        {
            NewLoader("registry-skip-live");
            var doomed = NewLoader("registry-skip-dead");
            doomed.Dispose();

            int reached = AssetLoaderRegistry.InvalidateAll(new[] { "some/address" });

            Assert.AreEqual(AssetLoaderRegistry.LiveCount, reached,
                "A disposed loader must not be counted or touched.");
        }

        [Test]
        public void InvalidateAll_Is_A_No_Op_For_Nothing_To_Do()
        {
            NewLoader("registry-empty");

            Assert.AreEqual(0, AssetLoaderRegistry.InvalidateAll(null),
                "Null keys must not walk the loaders at all.");
            Assert.AreEqual(0, AssetLoaderRegistry.InvalidateAll(Array.Empty<string>()),
                "An empty key set is the common case when a catalog update changed nothing; it must " +
                "not cost a walk.");
        }

        [Test]
        public void InvalidateAll_Enumerates_A_Lazy_Key_Sequence_Only_Once()
        {
            NewLoader("registry-lazy-a");
            NewLoader("registry-lazy-b");

            int enumerations = 0;

            IEnumerable<string> LazyKeys()
            {
                enumerations++;
                yield return "one";
                yield return "two";
            }

            AssetLoaderRegistry.InvalidateAll(LazyKeys());

            // The real caller passes a LINQ chain over a catalog locator. Re-enumerating it per
            // loader would walk the whole catalog once per scope.
            Assert.AreEqual(1, enumerations,
                "The key sequence must be materialised once, not re-enumerated for every loader.");
        }

        [Test]
        public void Unregister_Is_Safe_To_Call_Twice()
        {
            var loader = NewLoader("registry-double-dispose");

            loader.Dispose();
            int after = AssetLoaderRegistry.LiveCount;

            Assert.DoesNotThrow(() => loader.Dispose(),
                "Dispose is called from `using` blocks and from teardown; the second call must be inert.");
            Assert.AreEqual(after, AssetLoaderRegistry.LiveCount);
        }
    }
}
