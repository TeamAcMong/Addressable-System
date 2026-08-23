using NUnit.Framework;
using AddressableManager.Cdn;

namespace AddressableManager.Tests
{
    /// <summary>
    /// State transitions of the snapshot behind the Runtime Monitor tab's download row.
    /// </summary>
    /// <remarks>
    /// The row this feeds was permanently dead before 4.1.0-pre.15: the progress bar had exactly one
    /// assignment in the whole file and it set the value to zero, so a bar reading
    /// "No download in progress" sat there while content was visibly downloading. The failure mode
    /// being pinned here is the mirror image - a monitor that says "downloading" forever after a
    /// download ends is the same lie pointing the other way, and the failure path is the easy one to
    /// forget.
    /// </remarks>
    [TestFixture]
    public class CdnDownloadMonitorTests
    {
        [SetUp]
        public void SetUp()
        {
            // Static state, so every test starts from a known point rather than from whatever the
            // previous one left behind.
            CdnDownloadMonitor.Complete(default);
        }

        [Test]
        public void Report_MarksDownloadingAndKeepsTheValues()
        {
            CdnDownloadMonitor.Report(new DownloadProgress(512, 2048, 1024, 3));

            Assert.IsTrue(CdnDownloadMonitor.IsDownloading);
            Assert.AreEqual(512, CdnDownloadMonitor.Current.DownloadedBytes);
            Assert.AreEqual(2048, CdnDownloadMonitor.Current.TotalBytes);
            // 0.25, not 25: Percent is a FRACTION, and monitor-download-bar is declared
            // low-value="0" high-value="1" so it binds to the fraction directly. This assertion
            // asserted 25f in 4.1.0-pre.15 and failed - correctly, because the tab was dividing by
            // 100 to reach a scale nothing in the package uses. The consumer was the wrong side.
            Assert.AreEqual(0.25f, CdnDownloadMonitor.Current.Percent, 0.001f,
                "Percent is a 0..1 fraction and is what the progress bar binds to unconverted.");
        }

        [Test]
        public void Complete_WithFinalValues_StopsDownloadingButKeepsThemReadable()
        {
            CdnDownloadMonitor.Report(new DownloadProgress(512, 2048, 1024, 3));
            CdnDownloadMonitor.Complete(new DownloadProgress(2048, 2048, 1024, 0));

            Assert.IsFalse(CdnDownloadMonitor.IsDownloading);
            Assert.AreEqual(2048, CdnDownloadMonitor.Current.DownloadedBytes,
                "The final figures stay readable after completion - a UI polling once a second must " +
                "be able to show the finished total rather than snapping back to zero.");
        }

        [Test]
        public void Complete_AfterAFailure_ClearsDownloadingEvenWithNoFinalFigures()
        {
            CdnDownloadMonitor.Report(new DownloadProgress(512, 2048, 1024, 3));

            CdnDownloadMonitor.Complete();

            Assert.IsFalse(CdnDownloadMonitor.IsDownloading,
                "A failed download must clear the flag. Leaving it set would leave every UI reading " +
                "this reporting a download that is not running - the same frozen-state lie the monitor " +
                "was added to remove, just pointing the other way.");
        }
    }
}
