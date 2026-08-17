using AddressableManager.Cdn;
using NUnit.Framework;

namespace AddressableManager.Tests.Editor
{
    /// <summary>
    /// The two places a URL was compared or rewritten with the wrong string comparison.
    /// </summary>
    /// <remarks>
    /// Both defects came out of an external review of PR #3 and both were the same mistake wearing
    /// different clothes: reaching for a <c>string</c> overload that has no
    /// <see cref="System.StringComparison"/> parameter, and inheriting a default that is wrong for
    /// URLs.
    ///
    ///   HostRewriter.ResolveTokens   detected tokens with OrdinalIgnoreCase and then substituted
    ///                                with the two-argument Replace, which is case-SENSITIVE. So
    ///                                "{Platform}" was recognised and left in place, and the literal
    ///                                brace text travelled on into a request URL.
    ///
    ///   CdnEnvironment.IsValid       used StartsWith("http://") and EndsWith("/"), whose no-comparison
    ///                                overloads are CurrentCulture — so whether a URL was valid could
    ///                                depend on the machine's locale, and "HTTPS://..." was rejected
    ///                                outright even though URI schemes are case-insensitive.
    ///
    /// These are characterisation tests as much as regression tests. Neither behaviour was covered,
    /// which is why a detection/substitution mismatch sat in the canonical token expander unnoticed.
    ///
    /// Deliberately EditMode and deliberately not in Tests/Runtime: both methods under test are pure
    /// functions over strings. The Runtime fixtures need a packed PlayMode build and a local HTTP
    /// server, and a test that drags in a server to check a string comparison is a test nobody runs.
    /// </remarks>
    [TestFixture]
    public class CdnUrlCasingTests
    {
        // ---------------------------------------------------------------- ResolveTokens

        [Test]
        public void ResolveTokens_Replaces_Lowercase_Platform_Token()
        {
            var resolved = HostRewriter.ResolveTokens("https://cdn.example.com/{platform}");

            Assert.IsFalse(resolved.Contains("{platform}"),
                "The all-lowercase spelling is the documented one; if this fails nothing else here matters.");
        }

        [TestCase("{Platform}")]
        [TestCase("{PLATFORM}")]
        [TestCase("{pLaTfOrM}")]
        public void ResolveTokens_Replaces_Mixed_Case_Platform_Token(string token)
        {
            var resolved = HostRewriter.ResolveTokens("https://cdn.example.com/" + token + "/bundles");

            Assert.IsFalse(resolved.Contains(token),
                $"'{token}' was detected case-insensitively and then not replaced, so the braces " +
                "reach the CDN as literal path characters.");
            Assert.IsFalse(resolved.Contains("{"),
                "No brace of any casing may survive token expansion.");
        }

        [TestCase("{AppVersion}")]
        [TestCase("{APPVERSION}")]
        [TestCase("{appversion}")]
        public void ResolveTokens_Replaces_Mixed_Case_AppVersion_Token(string token)
        {
            var resolved = HostRewriter.ResolveTokens("https://cdn.example.com/v" + token);

            Assert.IsFalse(resolved.Contains(token));
            Assert.IsFalse(resolved.Contains("{"));
        }

        [Test]
        public void ResolveTokens_Replaces_Both_Tokens_In_One_Url_Whatever_The_Casing()
        {
            var resolved = HostRewriter.ResolveTokens(
                "https://cdn.example.com/{PLATFORM}/{appVersion}/bundles");

            Assert.IsFalse(resolved.Contains("{"), "Both tokens must go, not just the one that matched exactly.");
            Assert.IsFalse(resolved.Contains("}"));
        }

        [Test]
        public void ResolveTokens_Replaces_Every_Occurrence_Not_Just_The_First()
        {
            // The rewritten helper loops. A single IndexOf/Substring pass would leave the second one.
            var resolved = HostRewriter.ResolveTokens(
                "https://cdn.example.com/{platform}/x/{Platform}/y");

            Assert.IsFalse(resolved.Contains("{"), "A repeated token must be expanded at every position.");
        }

        [Test]
        public void ResolveTokens_Leaves_A_Url_Without_Tokens_Alone()
        {
            const string url = "https://cdn.example.com/StandaloneWindows64/bundles";

            Assert.AreEqual(url, HostRewriter.ResolveTokens(url));
        }

        [Test]
        public void ResolveTokens_Tolerates_Null_And_Empty()
        {
            Assert.IsNull(HostRewriter.ResolveTokens(null));
            Assert.AreEqual(string.Empty, HostRewriter.ResolveTokens(string.Empty));
        }

        [Test]
        public void ReplaceIgnoreCase_Returns_The_Same_Instance_When_Nothing_Matches()
        {
            // Not a micro-optimisation assertion: it pins the early-out that keeps this allocation-free
            // on the common path, so a later "simplification" to StringBuilder-always is visible here.
            const string source = "https://cdn.example.com/none";

            Assert.AreSame(source, HostRewriter.ReplaceIgnoreCase(source, "{platform}", "X"));
        }

        [Test]
        public void ReplaceIgnoreCase_Treats_A_Null_Replacement_As_Removal()
        {
            Assert.AreEqual("ab", HostRewriter.ReplaceIgnoreCase("a{T}b", "{t}", null),
                "A null Application.version must not throw or produce the string \"null\".");
        }

        // ---------------------------------------------------------------- IsValid

        [TestCase("http://cdn.example.com")]
        [TestCase("https://cdn.example.com/game")]
        public void IsValid_Accepts_A_Lowercase_Scheme(string url)
        {
            var env = new CdnEnvironment("Prod", "Prod", url);

            Assert.IsTrue(env.IsValid(out var reason), reason);
        }

        [TestCase("HTTPS://cdn.example.com")]
        [TestCase("HTTP://cdn.example.com")]
        [TestCase("Https://cdn.example.com/game")]
        public void IsValid_Accepts_An_Upper_Or_Mixed_Case_Scheme(string url)
        {
            // URI schemes are case-insensitive (RFC 3986 §3.1). This is reachable in practice:
            // CdnManager routes the CDN_BASE_URL environment-variable override through IsValid, and a
            // CI variable is typed by hand. Rejecting it left the build on the baked-in environment.
            var env = new CdnEnvironment("Prod", "Prod", url);

            Assert.IsTrue(env.IsValid(out var reason),
                $"'{url}' is a valid absolute URL and must not be rejected on casing. Reason given: {reason}");
        }

        [TestCase("ftp://cdn.example.com")]
        [TestCase("cdn.example.com")]
        [TestCase("file:///c:/tmp")]
        public void IsValid_Still_Rejects_A_Non_Http_Scheme(string url)
        {
            // The fix widens the comparison, not the accepted set.
            var env = new CdnEnvironment("Prod", "Prod", url);

            Assert.IsFalse(env.IsValid(out var reason));
            Assert.IsNotNull(reason);
        }

        [Test]
        public void IsValid_Still_Rejects_A_Trailing_Slash()
        {
            var env = new CdnEnvironment("Prod", "Prod", "https://cdn.example.com/");

            Assert.IsFalse(env.IsValid(out var reason));
            StringAssert.Contains("must not end with", reason);
        }

        [Test]
        public void IsValid_Rejects_An_Empty_Base_Url_And_An_Empty_Id()
        {
            Assert.IsFalse(new CdnEnvironment("Prod", "Prod", "").IsValid(out _));
            Assert.IsFalse(new CdnEnvironment("", "Prod", "https://cdn.example.com").IsValid(out _));
        }
    }
}
