using System.Collections.Generic;
using NUnit.Framework;
using AddressableManager.Editor.Windows.Hub;

namespace AddressableManager.Tests
{
    /// <summary>
    /// Contracts the hub shell depends on and that nothing else would catch.
    /// </summary>
    /// <remarks>
    /// These are cheap invariants with expensive failures. A duplicated section id makes routing pick
    /// whichever was registered first — silently, and only for the one deep link nobody clicked
    /// during testing. A stage with no sections is a step of the pipeline that cannot be drawn. A
    /// palette that ranks a subtitle hit above a title hit is one people stop trusting after the
    /// second time it puts the screen they named third.
    /// </remarks>
    [TestFixture]
    public class HubSectionContractTests
    {
        private List<IHubSection> _sections;

        [SetUp]
        public void SetUp()
        {
            _sections = HubSections.Create();
            Assert.IsNotNull(_sections, "HubSections.Create() returned null.");
            Assert.IsNotEmpty(_sections, "HubSections.Create() returned no sections.");
        }

        [Test]
        public void SectionIds_AreUniqueAndNonEmpty()
        {
            var seen = new HashSet<string>();

            foreach (var section in _sections)
            {
                Assert.IsFalse(string.IsNullOrEmpty(section.Id),
                    $"Section '{section.Title}' has no Id. Ids are persisted and used by deep links.");

                Assert.IsTrue(seen.Add(section.Id),
                    $"Duplicate section id '{section.Id}'. Routing would silently pick whichever was " +
                    "registered first.");
            }
        }

        [Test]
        public void EverySection_HasASubtitle()
        {
            foreach (var section in _sections)
            {
                Assert.IsFalse(string.IsNullOrEmpty(section.Subtitle),
                    $"Section '{section.Id}' has no Subtitle. Fourteen sections is past the point " +
                    "where a bare noun tells anyone what they are looking at.");
            }
        }

        [Test]
        public void EverySection_BelongsToADrawnStage()
        {
            foreach (var section in _sections)
            {
                Assert.Contains(section.Stage, PipelineStages.All,
                    $"Section '{section.Id}' has a stage the rail does not draw, so it would be " +
                    "unreachable.");
            }
        }

        /// <summary>Every stage must have somewhere to go, or the rail advertises a dead step.</summary>
        [Test]
        public void EveryPipelineStage_HasAtLeastOneSection()
        {
            foreach (var stage in PipelineStages.All)
            {
                bool covered = _sections.Exists(s => s.Stage == stage);
                Assert.IsTrue(covered,
                    $"Pipeline stage '{PipelineStages.Label(stage)}' has no sections, so the rail " +
                    "cannot draw it and that step of the pipeline is invisible.");
            }
        }

        /// <summary>
        /// Health must be stable and must never claim NotMeasured without saying why.
        /// </summary>
        /// <remarks>
        /// The rail calls GetHealth once a second for every section. One that throws would repeat
        /// forever and bury the Console; one that answers differently on consecutive calls makes the
        /// rail flicker between verdicts. And a NotMeasured with no reason is only marginally better
        /// than a false green — it tells the reader nothing about what to do next.
        /// </remarks>
        [Test]
        public void GetHealth_IsStableAndExplainsNotMeasured()
        {
            foreach (var section in _sections)
            {
                SectionHealth first, second;

                try
                {
                    first = section.GetHealth();
                    second = section.GetHealth();
                }
                catch (System.Exception ex)
                {
                    Assert.Fail($"Section '{section.Id}' threw from GetHealth: {ex.GetType().Name}: " +
                                $"{ex.Message}. The rail calls this once a second.");
                    return;
                }

                Assert.AreEqual(first.State, second.State,
                    $"Section '{section.Id}' reported {first.State} then {second.State} with nothing " +
                    "changed in between.");

                if (first.State == HealthState.NotMeasured)
                {
                    Assert.IsFalse(string.IsNullOrEmpty(first.Reason),
                        $"Section '{section.Id}' reports NotMeasured with no reason.");
                }
            }
        }

        /// <summary>NotMeasured must outrank Ok when rolling sections up into a stage.</summary>
        /// <remarks>
        /// A stage holding one unmeasured section is not a healthy stage; it is a stage nobody can
        /// vouch for. Getting this backwards would paint the rail green over an unknown, which is
        /// the exact failure the four-state model exists to prevent.
        /// </remarks>
        [Test]
        public void Worse_RanksNotMeasuredAboveOk()
        {
            Assert.AreEqual(HealthState.NotMeasured,
                SectionHealth.Worse(HealthState.Ok, HealthState.NotMeasured));

            Assert.AreEqual(HealthState.Warning,
                SectionHealth.Worse(HealthState.NotMeasured, HealthState.Warning));

            Assert.AreEqual(HealthState.Blocked,
                SectionHealth.Worse(HealthState.Warning, HealthState.Blocked));
        }

        /// <summary>NotMeasured cannot be constructed without a reason.</summary>
        [Test]
        public void NotMeasured_CarriesItsReason()
        {
            var health = SectionHealth.NotMeasured("nothing was running");

            Assert.AreEqual(HealthState.NotMeasured, health.State);
            Assert.AreEqual("nothing was running", health.Reason);
            Assert.AreNotEqual(SectionHealth.StyleClassFor(HealthState.Ok), health.StyleClass,
                "NotMeasured must not share a style class with Ok.");
        }

        /// <summary>A title match must always rank above a subtitle match.</summary>
        [Test]
        public void Palette_RanksTitleMatchesFirst()
        {
            var matches = HubPalette.Match(_sections, "rules");

            Assert.IsNotEmpty(matches, "'rules' should match at least the Layout Rules section.");
            StringAssert.Contains("Rules", matches[0].Title,
                "A title match must outrank a subtitle match, or the palette stops being trusted.");
        }

        /// <summary>Subsequence matching is the whole point of typing instead of scanning.</summary>
        [Test]
        public void Palette_MatchesSubsequences()
        {
            var matches = HubPalette.Match(_sections, "rm");

            bool foundMonitor = matches.Exists(s => s.Title == "Runtime Monitor");
            Assert.IsTrue(foundMonitor,
                "'rm' should reach 'Runtime Monitor' by subsequence. Substring-only matching would " +
                "mean typing the whole word, which is no faster than reading the rail.");
        }

        /// <summary>An empty query lists everything, in rail order.</summary>
        [Test]
        public void Palette_EmptyQueryListsEverything()
        {
            var matches = HubPalette.Match(_sections, string.Empty);
            Assert.AreEqual(_sections.Count, matches.Count);
        }
    }
}
