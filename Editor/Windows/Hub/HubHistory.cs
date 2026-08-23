using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AddressableManager.Editor.Windows.Hub
{
    /// <summary>What kind of thing happened.</summary>
    public enum HistoryKind
    {
        /// <summary>It worked.</summary>
        Ok = 0,

        /// <summary>It worked, with something worth reading.</summary>
        Warning = 1,

        /// <summary>It did not work.</summary>
        Failed = 2,
    }

    /// <summary>One recorded event.</summary>
    [Serializable]
    public sealed class HistoryEntry
    {
        /// <summary>When, as a round-trip UTC string. Stored as text so the file stays readable.</summary>
        public string utc;

        /// <summary>What happened, in the user's terms.</summary>
        public string what;

        /// <summary>The figure worth remembering — a size, a count, a duration. May be empty.</summary>
        public string figure;

        /// <summary>Outcome.</summary>
        public HistoryKind kind;
    }

    [Serializable]
    internal sealed class HistoryFile
    {
        public List<HistoryEntry> entries = new List<HistoryEntry>();
    }

    /// <summary>
    /// A short log of what this machine did: builds, rule applies, catalog checks.
    /// </summary>
    /// <remarks>
    /// Answers the one question nothing else in the package could: <i>what did I ship, and when?</i>
    /// A build leaves bundles and a manifest, but no record that anyone can read a week later, and
    /// "which build is live" was previously answered by looking at file timestamps.
    ///
    /// <b>Under Library/, deliberately.</b> This is a record of what happened on THIS machine, so it
    /// is per-machine by nature: putting it in the project would make every developer's local builds
    /// a source of merge conflicts, and a shared history that four people write to concurrently
    /// would be wrong more often than right. Library/ is already gitignored, already per-machine, and
    /// already the place Unity keeps things it can regenerate.
    ///
    /// The corollary is that it can be deleted at any time and nothing breaks - so nothing may depend
    /// on it, and nothing does. It is a convenience, not a system of record; CI keeps the real one.
    /// </remarks>
    public static class HubHistory
    {
        private const int MaxEntries = 40;

        private static string Directory => Path.Combine("Library", "com.game.addressables");
        private static string FilePath => Path.Combine(Directory, "history.json");

        /// <summary>Record an event. Never throws — a failed write must not take an action down with it.</summary>
        /// <remarks>
        /// Callers reach this from the finally block of a build or an apply. Letting an IO failure
        /// propagate from there would turn "the history file is read-only" into "the build reported
        /// an exception", which is a far worse lie than a missing log line.
        /// </remarks>
        public static void Record(HistoryKind kind, string what, string figure = null)
        {
            try
            {
                var file = Load();

                file.entries.Insert(0, new HistoryEntry
                {
                    utc = DateTime.UtcNow.ToString("o"),
                    what = what ?? string.Empty,
                    figure = figure ?? string.Empty,
                    kind = kind,
                });

                if (file.entries.Count > MaxEntries)
                    file.entries.RemoveRange(MaxEntries, file.entries.Count - MaxEntries);

                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllText(FilePath, JsonUtility.ToJson(file, true));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AddressableManager] Could not write the history file: {ex.Message}");
            }
        }

        /// <summary>The most recent entries, newest first. Empty when there is no history.</summary>
        public static List<HistoryEntry> Recent(int count)
        {
            var file = Load();
            if (file.entries.Count <= count) return file.entries;

            return file.entries.GetRange(0, count);
        }

        /// <summary>True when anything has ever been recorded on this machine.</summary>
        /// <remarks>
        /// Lets a UI distinguish "nothing has happened" from "the log was deleted" - which are the
        /// same to a reader looking at an empty list, and only one of them means the tool is new to
        /// this machine.
        /// </remarks>
        public static bool Exists => File.Exists(FilePath);

        private static HistoryFile Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new HistoryFile();

                var loaded = JsonUtility.FromJson<HistoryFile>(File.ReadAllText(FilePath));

                // A truncated or hand-edited file deserialises to null or to an object with no list.
                // Starting fresh is right: this is a convenience log, and refusing to open the window
                // because its scratch file is malformed would be the tail wagging the dog.
                return loaded != null && loaded.entries != null ? loaded : new HistoryFile();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AddressableManager] Could not read the history file: {ex.Message}");
                return new HistoryFile();
            }
        }
    }
}
