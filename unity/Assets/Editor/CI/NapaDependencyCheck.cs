using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace CI
{
    /// <summary>Keep missing/stale native dependencies from silently shipping a fallback detector.</summary>
    public sealed class NapaDependencyCheck : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        [Serializable]
        private sealed class DependencyLock
        {
            public Artifact[] artifacts;
        }

        [Serializable]
        private sealed class Artifact
        {
            public DependencyFile[] files;
        }

        [Serializable]
        private sealed class DependencyFile
        {
            public string destination;
            public string sha256;
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            if (
                report.summary.platform != BuildTarget.Android
                && report.summary.platform != BuildTarget.StandaloneWindows64
            )
                return;

            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            const string restore =
                "Run python native/napa/restore.py from the repository root with Unity closed.";
            string lockPath = Path.Combine(root, "native/napa/dependency.lock.json");
            if (!File.Exists(lockPath))
                throw new BuildFailedException($"NAPA dependency lock is missing. {restore}");

            var dependency = JsonUtility.FromJson<DependencyLock>(File.ReadAllText(lockPath));
            if (dependency?.artifacts == null || dependency.artifacts.Length == 0)
                throw new BuildFailedException("NAPA dependency lock has no artifacts.");

            using var sha = SHA256.Create();
            foreach (var artifact in dependency.artifacts)
            {
                if (artifact?.files == null || artifact.files.Length == 0)
                    throw new BuildFailedException(
                        "NAPA dependency lock contains an artifact without files. "
                            + "Restore native/napa/dependency.lock.json from version control."
                    );
                foreach (var file in artifact.files)
                {
                    string path = Path.Combine(root, file.destination);
                    if (!File.Exists(path))
                        throw new BuildFailedException(
                            $"NAPA dependency missing: {file.destination}. {restore}"
                        );
                    using var stream = File.OpenRead(path);
                    string hash = BitConverter
                        .ToString(sha.ComputeHash(stream))
                        .Replace("-", "")
                        .ToLowerInvariant();
                    if (hash != file.sha256)
                        throw new BuildFailedException(
                            $"NAPA dependency checksum mismatch: {file.destination}. {restore}"
                        );
                }
            }
        }
    }
}
