using System;
using System.Diagnostics;
using UnityEngine;

namespace AddressableManager.Editor.Providers
{
    /// <summary>
    /// Provides version based on Git commit hash or tag
    /// Requires Git to be installed and repository to be initialized
    /// </summary>
    [CreateAssetMenu(fileName = "GitCommitVersionProvider", menuName = "Addressable Manager/Providers/Version/Git Commit")]
    public class GitCommitVersionProvider : VersionProviderBase
    {
        public enum GitVersionMode
        {
            CommitHash,         // Short commit hash (e.g., "a1b2c3d")
            CommitHashFull,     // Full commit hash
            LatestTag,          // Latest git tag (e.g., "v1.0.0")
            TagOrHash,          // Tag if exists, otherwise hash
            Describe            // Git describe (e.g., "v1.0.0-5-ga1b2c3d")
        }

        [Header("Git Version Settings")]
        [Tooltip("Mode for generating version from Git")]
        [SerializeField] private GitVersionMode _mode = GitVersionMode.TagOrHash;

        [Tooltip("Prefix to add to version")]
        [SerializeField] private string _prefix = "";

        [Tooltip("Suffix to add to version")]
        [SerializeField] private string _suffix = "";

        [Tooltip("Fallback version if Git is not available")]
        [SerializeField] private string _fallbackVersion = "0.0.0-unknown";

        private string _cachedVersion;
        private bool _versionCached;

        public override void Setup()
        {
            base.Setup();

            // Cache version on setup to avoid running git command repeatedly
            _cachedVersion = GenerateVersionFromGit();
            _versionCached = true;
        }

        public override string Provide(string assetPath)
        {
            if (!_versionCached)
            {
                Setup();
            }

            return _cachedVersion;
        }

        private string GenerateVersionFromGit()
        {
            try
            {
                string version = _mode switch
                {
                    GitVersionMode.CommitHash => GetCommitHash(@short: true),
                    GitVersionMode.CommitHashFull => GetCommitHash(@short: false),
                    GitVersionMode.LatestTag => GetLatestTag(),
                    GitVersionMode.TagOrHash => GetTagOrHash(),
                    GitVersionMode.Describe => GetGitDescribe(),
                    _ => _fallbackVersion
                };

                if (string.IsNullOrEmpty(version))
                {
                    UnityEngine.Debug.LogWarning($"[GitCommitVersionProvider] Failed to get Git version, using fallback: {_fallbackVersion}");
                    return _fallbackVersion;
                }

                return $"{_prefix}{version}{_suffix}";
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[GitCommitVersionProvider] Error getting Git version: {ex.Message}");
                return _fallbackVersion;
            }
        }

        private string GetCommitHash(bool @short)
        {
            string arg = @short ? "rev-parse --short HEAD" : "rev-parse HEAD";
            return ExecuteGitCommand(arg);
        }

        private string GetLatestTag()
        {
            return ExecuteGitCommand("describe --tags --abbrev=0");
        }

        private string GetTagOrHash()
        {
            // Try to get tag first
            string tag = GetLatestTag();
            if (!string.IsNullOrEmpty(tag))
            {
                return tag;
            }

            // Fall back to commit hash
            return GetCommitHash(@short: true);
        }

        private string GetGitDescribe()
        {
            return ExecuteGitCommand("describe --tags --always --dirty");
        }

        private string ExecuteGitCommand(string arguments)
        {
            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Application.dataPath
                };

                using (var process = Process.Start(processInfo))
                {
                    if (process == null)
                    {
                        return null;
                    }

                    string output = process.StandardOutput.ReadToEnd().Trim();
                    string error = process.StandardError.ReadToEnd().Trim();

                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        if (!string.IsNullOrEmpty(error))
                        {
                            UnityEngine.Debug.LogWarning($"[GitCommitVersionProvider] Git command failed: {error}");
                        }
                        return null;
                    }

                    return output;
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[GitCommitVersionProvider] Failed to execute git command: {ex.Message}");
                return null;
            }
        }

        public override string GetDisplayName()
        {
            if (!string.IsNullOrEmpty(_description))
                return _description;

            return $"Version: Git {_mode}";
        }

        private void OnValidate()
        {
            // Invalidate cache when settings change
            _versionCached = false;
        }
    }
}
