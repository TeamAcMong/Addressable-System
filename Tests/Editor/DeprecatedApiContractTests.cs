using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AddressableManager.API;
using AddressableManager.Core;
using AddressableManager.Loaders;
using AddressableManager.Managers;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static AddressableManager.Tests.Editor.LoaderCacheProbe;

// Simple.Release<T> is one of the two subjects here, so CS0618 is suppressed file-wide.
#pragma warning disable 618

namespace AddressableManager.Tests.Editor
{
    /// <summary>
    /// A-9 (<c>Simple.Release&lt;T&gt;</c>) and A-10 (<c>Standard.ClearCache</c>): two log-and-return
    /// stubs, resolved in opposite directions in the same wave.
    /// </summary>
    /// <remarks>
    /// The two started as the same defect — a method whose only effect was a log line, on an API
    /// whose docs advertised it as doing real work — and their fixes diverged because only one of
    /// them was implementable.
    ///
    /// A-9 <b>could not</b> be: there is no path from an asset instance back to the cache entry
    /// holding it, and inventing one means a reverse map <see cref="AssetLoader"/> does not have.
    /// So it is <c>[Obsolete]</c> per invariant 6, pointing at <c>Simple.ReleaseAddress</c> — the
    /// same operation with the one piece of information that makes it possible. Asserting "it is
    /// deprecated and its named replacement exists" is the whole contract; asserting it is still a
    /// no-op would be asserting the bug.
    ///
    /// A-10 <b>could</b> be, because <c>ScopeManager.GetScope</c> already existed and already
    /// returned the loader. So it is implemented, and the assertions below are about behaviour: it
    /// clears the right thing, it does not dispose what it was not asked to dispose, and an id
    /// nothing is registered under is an error rather than the silence a caller under memory
    /// pressure would read as success.
    /// </remarks>
    [TestFixture]
    public class DeprecatedApiContractTests
    {
        private const string TestScopeId = "A10-ClearCacheTests";

        private readonly List<string> _log = new List<string>();

        [SetUp]
        public void SetUp()
        {
            // Both subjects are DEFINED partly in terms of what they log — a warning that fires once
            // per process, an error naming the registered scopes — so the text is captured here and
            // asserted on directly.
            _log.Clear();
            Application.logMessageReceived += Capture;
        }

        [TearDown]
        public void TearDown()
        {
            Application.logMessageReceived -= Capture;

            if (ScopeManager.Instance.HasScope(TestScopeId))
            {
                ScopeManager.Instance.ClearScope(TestScopeId);
            }
        }

        private void Capture(string condition, string stackTrace, LogType type) => _log.Add(condition);

        private int CountLogs(string fragment) =>
            _log.Count(line => line.IndexOf(fragment, StringComparison.Ordinal) >= 0);

        private string FirstLog(string fragment) =>
            _log.FirstOrDefault(line => line.IndexOf(fragment, StringComparison.Ordinal) >= 0);

        /// <summary>
        /// Let this test provoke <c>Debug.LogError</c> without the framework failing it before the
        /// assertion that reads the message can run.
        /// </summary>
        /// <remarks>
        /// MUST be called from the test body. Setting <see cref="LogAssert.ignoreFailingMessages"/>
        /// in <c>[SetUp]</c> looks equivalent and is not: the log scope a test is judged against is
        /// opened after <c>[SetUp]</c> has run, so the value lands on the wrong scope and the test
        /// still fails on the first error line. The framework resets the flag after each test, so
        /// there is nothing to restore. Every caller below still asserts on the message text.
        /// </remarks>
        private static void ErrorLogsAreExpectedHere() => LogAssert.ignoreFailingMessages = true;

        // ================================================================== A-9

        [Test]
        public void A9_Simple_Release_Is_Obsolete_And_Points_At_A_Replacement_That_Exists()
        {
            var release = typeof(Simple).GetMethod("Release", BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(release, "Simple.Release<T> must still exist — invariant 6 keeps it to 5.0.0.");

            var obsolete = release.GetCustomAttribute<ObsoleteAttribute>();

            Assert.IsNotNull(obsolete,
                "A-9 went the [Obsolete] way rather than the reverse-map way, so the deprecation " +
                "attribute IS the fix. Asserting a no-op here instead would be asserting the bug.");
            Assert.IsFalse(obsolete.IsError,
                "Warning, not error: 4.x source that calls it must still compile until 5.0.0.");
            StringAssert.Contains("ReleaseAddress", obsolete.Message,
                "The message has to name where to go, not just say not to come here.");
            StringAssert.Contains("5.0.0", obsolete.Message);

            var replacement = typeof(Simple).GetMethod("ReleaseAddress", new[] { typeof(string) });

            Assert.IsNotNull(replacement,
                "The named replacement has to exist. An [Obsolete] pointing at nothing is a worse " +
                "state than the stub it replaced — the caller now knows the old way is wrong and " +
                "still has no right way.");
            Assert.IsNull(replacement.GetCustomAttribute<ObsoleteAttribute>(),
                "...and it must not itself be deprecated.");
            Assert.AreEqual(typeof(void), replacement.ReturnType,
                "void deliberately: a bool would be a sentinel meaning both 'nothing was cached' " +
                "and 'there is no scope', which invariant 4 forbids. IsLoaded answers that question.");
        }

        [Test]
        public void A9_Simple_Release_Warns_Once_Per_Process_Instead_Of_Logging_On_Every_Call()
        {
            ResetReleaseWarnLatch();

            Simple.Release(new object());

            Assert.AreEqual(1, CountLogs("Released nothing"),
                "Half of A-9 was the log itself: a Debug.Log that ran in shipping builds, once per " +
                "call, forever, reporting work that never happened. It is a warning now — the " +
                "severity the situation actually has.");

            Simple.Release(new object());
            Simple.Release("a different type entirely");

            Assert.AreEqual(1, CountLogs("Released nothing"),
                "...and latched, so it names the problem once rather than becoming per-frame spam " +
                "in a loop that releases assets.");
        }

        [Test]
        public void A9_Simple_Release_Does_Not_Throw_For_Any_Argument()
        {
            ResetReleaseWarnLatch();

            // It is on a shipping path in 4.x code. Deprecated or not, it has to stay inert rather
            // than becoming a new crash for anyone who has not migrated yet.
            Assert.DoesNotThrow(() => Simple.Release<object>(null));
            Assert.DoesNotThrow(() => Simple.Release(new object()));
        }

        /// <summary>
        /// Clear the warn-once latch so this fixture does not depend on whether some earlier test —
        /// in this run or another fixture — already burned it.
        /// </summary>
        private static void ResetReleaseWarnLatch()
        {
            var latch = typeof(Simple)
                .GetField("_releaseNoOpWarned", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(latch,
                "Simple._releaseNoOpWarned is the once-per-process latch under test. If it was " +
                "renamed, update this helper — the 'logged once' half of A-9 is not testable " +
                "without resetting it.");

            latch.SetValue(null, false);
        }

        // ================================================================== A-10

        [Test]
        public void A10_Standard_ClearCache_Is_Implemented_Rather_Than_Deprecated()
        {
            var clear = typeof(Standard).GetMethod("ClearCache", new[] { typeof(string) });

            Assert.IsNotNull(clear);
            Assert.IsNull(clear.GetCustomAttribute<ObsoleteAttribute>(),
                "A-10 went the other way from A-9: ScopeManager.GetScope already existed and " +
                "already returned the loader, so the stub was implementable and was implemented.");
            Assert.AreEqual(typeof(void), clear.ReturnType,
                "Still void, for the same reason ScopeManager.ClearScope is: no return value means " +
                "no sentinel to get wrong.");
        }

        [Test]
        public void A10_Clearing_An_Unregistered_Scope_Is_An_Error_Naming_What_Is_Registered()
        {
            ErrorLogsAreExpectedHere();

            ScopeManager.Instance.GetOrCreateScope(TestScopeId);

            Standard.ClearCache("no-such-scope");

            Assert.AreEqual(1, CountLogs("No scope is registered as 'no-such-scope'"),
                "The stub returned silently for every id. A caller clearing a scope under memory " +
                "pressure must never read silence as success — that is the whole of A-10.");

            // Read the error line itself rather than counting the id across the whole log:
            // GetOrCreateScope logs the same id when it creates the scope.
            StringAssert.Contains(TestScopeId, FirstLog("No scope is registered as 'no-such-scope'"),
                "The error names what IS registered, so a typo is diagnosable from the log line " +
                "alone rather than needing a debugger.");
        }

        [Test]
        public void A10_Clearing_A_Null_Or_Empty_Scope_Name_Is_An_Error_And_Not_A_Throw()
        {
            ErrorLogsAreExpectedHere();

            Assert.DoesNotThrow(() => Standard.ClearCache(null));
            Assert.DoesNotThrow(() => Standard.ClearCache(string.Empty));

            Assert.AreEqual(2, CountLogs("Scope name is null or empty"),
                "Reported, not thrown: this is reachable from a config-driven call site, and a " +
                "throw there is a crash where a diagnosable log is enough.");
        }

        [Test]
        public void A10_Clearing_A_Registered_Scope_Empties_Its_Cache()
        {
            var loader = ScopeManager.Instance.GetOrCreateScope(TestScopeId);
            Assert.IsNotNull(loader);

            var handle = Plant(loader, "some/asset", 4096, CacheTier.Hot);
            SetTieredBytes(loader, 4096);

            Standard.ClearCache(TestScopeId);

            Assert.AreEqual(0, CacheOf(loader).Count,
                "The comment on the old stub said 'implementation depends on scope manager'. It " +
                "does not any more — ScopeManager.GetScope returns this exact loader and the call " +
                "reaches its cache.");
            Assert.AreEqual(1, handle.ForceReleases,
                "ClearCache is the documented memory-pressure contract: an unconditional release, " +
                "so a caller still holding a handle across it sees IsValid == false rather than a " +
                "live-looking handle pointing at a freed asset.");
            Assert.AreEqual(0, CountLogs("No scope is registered"),
                "A registered id must not take the error path.");
        }

        [Test]
        public void A10_Clearing_A_Registered_Scope_Does_Not_Dispose_Or_Deregister_It()
        {
            var loader = ScopeManager.Instance.GetOrCreateScope(TestScopeId);

            Standard.ClearCache(TestScopeId);

            Assert.IsTrue(ScopeManager.Instance.HasScope(TestScopeId),
                "ClearCache, not ClearScope. ClearScope disposes a manager-owned loader and removes " +
                "the entry — the EndSession semantics, a strictly larger operation than this " +
                "method's name promises.");
            Assert.AreSame(loader, ScopeManager.Instance.GetScope(TestScopeId),
                "The same loader, still usable: the next load re-fetches into it rather than " +
                "finding a disposed object.");

            Assert.DoesNotThrow(() => Plant(loader, "after/clear", 1024, CacheTier.Hot),
                "A loader the caller can still cache into is the observable difference between " +
                "clearing a cache and ending a scope.");
        }

        [Test]
        public void A10_Reach_Is_Exactly_The_ScopeManager_Directory()
        {
            ErrorLogsAreExpectedHere();

            var loader = ScopeManager.Instance.GetOrCreateScope(TestScopeId);

            CollectionAssert.Contains(ScopeManager.Instance.ActiveScopes.ToArray(), TestScopeId,
                "ActiveScopes is the directory the docstring promises reach over, and it is what " +
                "the error path prints. If the two could disagree the message would be a lie.");
            Assert.AreSame(loader, ScopeManager.Instance.GetScope(TestScopeId));

            // A loader outside the directory is deliberately unreachable — the Facade's pool loader
            // is registered nowhere precisely so a bulk clear cannot yank a live pool's template
            // prefab out from under it.
            using (var unregistered = new AssetLoader("A10-NotInTheDirectory"))
            {
                var handle = Plant(unregistered, "pool/template", 2048, CacheTier.Hot);

                Standard.ClearCache("A10-NotInTheDirectory");

                Assert.AreEqual(1, CountLogs("No scope is registered as 'A10-NotInTheDirectory'"));
                Assert.AreEqual(0, handle.ForceReleases,
                    "Not in the directory means not reached — stated in the docstring and true in " +
                    "the code, rather than one of the two.");
            }
        }
    }
}

#pragma warning restore 618
