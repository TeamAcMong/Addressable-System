using NUnit.Framework;
using AddressableManager.Core;
using AddressableManager.Loaders;

namespace AddressableManager.Tests
{
    /// <summary>
    /// CANARY TEST SUITE FOR ADDRESSABLES UPGRADE
    ///
    /// This test class documents the CURRENT behavior of AssetLoader.ClassifyErrorMessage()
    /// as a baseline against Addressables 2.3.1 exception message strings.
    ///
    /// WARNING: These tests will FAIL if Unity rewords exception messages in Addressables 2.9.x+.
    /// A failure after upgrading Addressables means the substring matcher needs revisiting against
    /// the actual message format in the new version — not that the test is wrong.
    ///
    /// See CDN_SYSTEM.html §8 "Error Model" and §3.7 "Error Mapping".
    /// Current substring matching is a known flaw that will be replaced with
    /// status-code based classification in Phase 3. Until then, these tests
    /// ensure the current implementation stays consistent.
    /// </summary>
    [TestFixture]
    public class DetermineErrorCodeCharacterizationTests
    {
        // ============================================================================
        // TEST CASES: Each tests a specific branch or edge case
        // ============================================================================

        /// <summary>
        /// Test null exception message case.
        /// ClassifyErrorMessage(null) should return OperationFailed.
        /// Expectation: LoadErrorCode.OperationFailed
        /// </summary>
        [Test]
        public void NullMessage_ReturnsOperationFailed()
        {
            var result = AssetLoader.ClassifyErrorMessage(null);
            Assert.AreEqual(LoadErrorCode.OperationFailed, result,
                "Null message should return OperationFailed");
        }

        /// <summary>
        /// Test "not found" message.
        /// Real Addressables 2.3.1 message: "Asset not found: [address]"
        /// Found in: Unity.Addressables ResourceManager.LoadAssetAsync error handling
        /// Expectation: LoadErrorCode.AssetNotFound
        /// </summary>
        [Test]
        public void Message_AssetNotFound_ReturnsAssetNotFound()
        {
            var result = AssetLoader.ClassifyErrorMessage("Asset not found: SomeAddress");
            Assert.AreEqual(LoadErrorCode.AssetNotFound, result);
        }

        /// <summary>
        /// Test "no location" message.
        /// Real Addressables 2.3.1 message: "No location found for key: [key]"
        /// Found in: Addressables key resolution failure path
        /// Expectation: LoadErrorCode.AssetNotFound
        /// </summary>
        [Test]
        public void Message_NoLocation_ReturnsAssetNotFound()
        {
            var result = AssetLoader.ClassifyErrorMessage("No location found for key: MyKey");
            Assert.AreEqual(LoadErrorCode.AssetNotFound, result);
        }

        /// <summary>
        /// Test case insensitivity: "NOT FOUND" (uppercase).
        /// Message is lowercased before matching.
        /// Expectation: LoadErrorCode.AssetNotFound
        /// </summary>
        [Test]
        public void Message_NotFound_UpperCase_ReturnsAssetNotFound()
        {
            var result = AssetLoader.ClassifyErrorMessage("ASSET NOT FOUND: SomeAddress");
            Assert.AreEqual(LoadErrorCode.AssetNotFound, result,
                "Message is lowercased before matching, so uppercase variants match");
        }

        /// <summary>
        /// Test "invalid key" message.
        /// Real Addressables 2.3.1 message: "Invalid key"
        /// Found in: Key validation during load operation
        /// Expectation: LoadErrorCode.InvalidAddress
        /// </summary>
        [Test]
        public void Message_InvalidKey_ReturnsInvalidAddress()
        {
            var result = AssetLoader.ClassifyErrorMessage("Invalid key provided");
            Assert.AreEqual(LoadErrorCode.InvalidAddress, result);
        }

        /// <summary>
        /// Test "invalid address" message.
        /// Real message: "Invalid Address" (note capitalization in source)
        /// Found in: Addressables address validation
        /// Expectation: LoadErrorCode.InvalidAddress
        /// </summary>
        [Test]
        public void Message_InvalidAddress_ReturnsInvalidAddress()
        {
            var result = AssetLoader.ClassifyErrorMessage("Invalid Address: null or empty");
            Assert.AreEqual(LoadErrorCode.InvalidAddress, result);
        }

        /// <summary>
        /// Test "type" AND "mismatch" message.
        /// Note: BOTH substrings must be present (AND condition).
        /// Real message: "Type mismatch: expected [T1] but got [T2]"
        /// Found in: Type checking during asset load
        /// Expectation: LoadErrorCode.TypeMismatch
        /// </summary>
        [Test]
        public void Message_TypeMismatch_BothWordsPresent_ReturnsTypeMismatch()
        {
            var result = AssetLoader.ClassifyErrorMessage(
                "Type mismatch: expected Texture2D but got Material");
            Assert.AreEqual(LoadErrorCode.TypeMismatch, result);
        }

        /// <summary>
        /// QUIRK TEST: Message with "type" but NOT "mismatch".
        /// The matcher requires BOTH words (AND condition).
        /// A message with only "type" will fall through.
        /// This is a known flaw in substring matching but encoded as-is.
        /// Expectation: LoadErrorCode.OperationFailed (falls through to default)
        /// </summary>
        [Test]
        public void Message_TypeWithoutMismatch_ReturnsOperationFailed()
        {
            var result = AssetLoader.ClassifyErrorMessage("Invalid type found in asset");
            Assert.AreEqual(LoadErrorCode.OperationFailed, result,
                "QUIRK: 'type' alone doesn't match (needs both 'type' AND 'mismatch'). " +
                "This is a known flaw; will be fixed in Phase 3.");
        }

        /// <summary>
        /// Test "network" message.
        /// Real message: "Network error" or similar
        /// Found in: Network/download failure handling in Addressables
        /// Expectation: LoadErrorCode.NetworkError
        /// </summary>
        [Test]
        public void Message_Network_ReturnsNetworkError()
        {
            var result = AssetLoader.ClassifyErrorMessage("Network error: Failed to download");
            Assert.AreEqual(LoadErrorCode.NetworkError, result);
        }

        /// <summary>
        /// Test "connection" message (alternative to "network").
        /// Synthetic message (not confirmed in Addressables 2.3.1 source).
        /// Expectation: LoadErrorCode.NetworkError
        /// </summary>
        [Test]
        public void Message_Connection_ReturnsNetworkError()
        {
            var result = AssetLoader.ClassifyErrorMessage("Connection failed to remote host");
            Assert.AreEqual(LoadErrorCode.NetworkError, result);
        }

        /// <summary>
        /// Test "download" message (third alternative).
        /// Synthetic message (not confirmed in Addressables 2.3.1 source).
        /// Expectation: LoadErrorCode.NetworkError
        /// </summary>
        [Test]
        public void Message_Download_ReturnsNetworkError()
        {
            var result = AssetLoader.ClassifyErrorMessage("Download failed: HTTP 503");
            Assert.AreEqual(LoadErrorCode.NetworkError, result);
        }

        /// <summary>
        /// Test fallback case: message matching no patterns.
        /// A generic error message that contains none of the recognized patterns.
        /// Expectation: LoadErrorCode.OperationFailed (catch-all)
        /// </summary>
        [Test]
        public void Message_Unrecognized_ReturnsOperationFailed()
        {
            var result = AssetLoader.ClassifyErrorMessage("Something went wrong");
            Assert.AreEqual(LoadErrorCode.OperationFailed, result,
                "Unrecognized message falls through to OperationFailed");
        }

        /// <summary>
        /// PRECEDENCE TEST: Message matching multiple branches.
        /// If-chain order matters: "not found" / "no location" is checked first.
        /// Message: "No location found for key, type mismatch occurred" matches BOTH AssetNotFound AND TypeMismatch.
        /// Expectation: LoadErrorCode.AssetNotFound (because it's checked first)
        /// Precedence: AssetNotFound > InvalidAddress > TypeMismatch > NetworkError
        /// </summary>
        [Test]
        public void Message_MultipleMatches_ReturnsFirstInChain()
        {
            var result = AssetLoader.ClassifyErrorMessage(
                "No location found, type mismatch occurred");
            Assert.AreEqual(LoadErrorCode.AssetNotFound, result,
                "When multiple branches match, the first in the if-chain wins (AssetNotFound)");
        }

        /// <summary>
        /// PRECEDENCE TEST: InvalidAddress vs TypeMismatch.
        /// Message contains "invalid key" and "type mismatch".
        /// Expectation: LoadErrorCode.InvalidAddress (checked before TypeMismatch)
        /// </summary>
        [Test]
        public void Message_InvalidKeyAndTypeMismatch_ReturnsInvalidAddress()
        {
            var result = AssetLoader.ClassifyErrorMessage(
                "Invalid key and type mismatch in asset");
            Assert.AreEqual(LoadErrorCode.InvalidAddress, result,
                "InvalidAddress is checked before TypeMismatch");
        }

        /// <summary>
        /// PRECEDENCE TEST: InvalidAddress vs NetworkError.
        /// Message contains "invalid address" and "network".
        /// Expectation: LoadErrorCode.InvalidAddress (checked before NetworkError)
        /// </summary>
        [Test]
        public void Message_InvalidAddressAndNetwork_ReturnsInvalidAddress()
        {
            var result = AssetLoader.ClassifyErrorMessage(
                "Invalid address network error");
            Assert.AreEqual(LoadErrorCode.InvalidAddress, result);
        }

        /// <summary>
        /// Edge case: Empty string message.
        /// Expectation: LoadErrorCode.OperationFailed (no patterns match)
        /// </summary>
        [Test]
        public void Message_Empty_ReturnsOperationFailed()
        {
            var result = AssetLoader.ClassifyErrorMessage("");
            Assert.AreEqual(LoadErrorCode.OperationFailed, result);
        }

        /// <summary>
        /// Edge case: Message with substring but different spacing.
        /// Message contains "notfound" (no space) instead of "not found" (with space).
        /// Expectation: LoadErrorCode.OperationFailed (no match due to spacing)
        /// This is a fragility point: if Addressables rewrites with different spacing, tests break.
        /// </summary>
        [Test]
        public void Message_PartialWordNoSpace_ReturnsOperationFailed()
        {
            var result = AssetLoader.ClassifyErrorMessage("Asset notfound in registry");
            Assert.AreEqual(LoadErrorCode.OperationFailed, result,
                "Substring 'notfound' (no space) does not match 'not found' (with space). " +
                "This is a fragility: if Addressables changes spacing, matching breaks.");
        }

        /// <summary>
        /// Edge case: Partial word match within other word.
        /// Message contains "download" as part of "downloads" (plural).
        /// Contains() should match regardless.
        /// Expectation: LoadErrorCode.NetworkError
        /// </summary>
        [Test]
        public void Message_PluralWord_ReturnsNetworkError()
        {
            var result = AssetLoader.ClassifyErrorMessage("Multiple downloads failed");
            Assert.AreEqual(LoadErrorCode.NetworkError, result,
                "Contains() matches 'download' within 'downloads'");
        }

        /// <summary>
        /// Test case sensitivity boundary: "NOT FOUND" vs "not found".
        /// Verifies that ToLower() makes matching case-insensitive.
        /// Expectation: LoadErrorCode.AssetNotFound
        /// </summary>
        [Test]
        public void Message_MixedCase_ReturnsAssetNotFound()
        {
            var result = AssetLoader.ClassifyErrorMessage("Asset Not Found: test");
            Assert.AreEqual(LoadErrorCode.AssetNotFound, result);
        }

        /// <summary>
        /// Test whitespace variations: Extra spaces around keywords.
        /// Message: "  not  found  " (multiple spaces)
        /// Expectation: LoadErrorCode.AssetNotFound (substring match is lenient)
        /// </summary>
        [Test]
        public void Message_ExtraWhitespace_StillMatches()
        {
            var result = AssetLoader.ClassifyErrorMessage("Error:  not  found  in cache");
            Assert.AreEqual(LoadErrorCode.AssetNotFound, result);
        }

        /// <summary>
        /// Test message with multiple matching keywords from same branch.
        /// Message: "Asset not found, no location available" (both "not found" AND "no location")
        /// Both trigger the same AssetNotFound branch (OR condition).
        /// Expectation: LoadErrorCode.AssetNotFound
        /// </summary>
        [Test]
        public void Message_BothKeywordsFromSameBranch_ReturnsAssetNotFound()
        {
            var result = AssetLoader.ClassifyErrorMessage(
                "Asset not found, no location available");
            Assert.AreEqual(LoadErrorCode.AssetNotFound, result,
                "Both 'not found' and 'no location' are in the same branch (OR), so either matches");
        }

        /// <summary>
        /// Test TypeMismatch precedence over NetworkError.
        /// Message: "Type mismatch in download network connection"
        /// Contains: "type", "mismatch", "network", "connection", "download"
        /// Precedence: TypeMismatch (checked 3rd) vs NetworkError (checked 4th)
        /// Expectation: LoadErrorCode.TypeMismatch (checked first)
        /// </summary>
        [Test]
        public void Message_TypeMismatchAndNetwork_ReturnsTypeMismatch()
        {
            var result = AssetLoader.ClassifyErrorMessage(
                "Type mismatch in download network connection");
            Assert.AreEqual(LoadErrorCode.TypeMismatch, result,
                "TypeMismatch (3rd check) wins over NetworkError (4th check)");
        }

        /// <summary>
        /// Test AssetNotFound precedence over InvalidAddress.
        /// Message: "Asset not found invalid address specified"
        /// Contains: "not found" and "invalid address"
        /// Precedence: AssetNotFound (1st) vs InvalidAddress (2nd)
        /// Expectation: LoadErrorCode.AssetNotFound (checked first)
        /// </summary>
        [Test]
        public void Message_NotFoundAndInvalidAddress_ReturnsAssetNotFound()
        {
            var result = AssetLoader.ClassifyErrorMessage(
                "Asset not found invalid address specified");
            Assert.AreEqual(LoadErrorCode.AssetNotFound, result,
                "AssetNotFound (1st check) wins over InvalidAddress (2nd check)");
        }
    }
}
