#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace AudioSystem.UpdateToPaid.Editor
{
    /// <summary>
    /// PUBLISHING-SIDE tool (Slice 1, docs/UpdateToPaid/PLAN.md §2). Run this in a project that
    /// contains the FREE package's precompiled DLLs (release_free_package.ps1's test project, or any
    /// project with the free package installed). It dumps every DLL script class's exact serialized
    /// identity — <c>(dll guid, local fileID, fully-qualified type name)</c> — into
    /// <c>UpgradeMap.json</c>, which ships in this upgrade package and drives the Upgrade From Free
    /// window's rewrite.
    ///
    /// <para>Identities are read from Unity itself (<see cref="AssetDatabase.TryGetGUIDAndLocalFileIdentifier(UnityEngine.Object, out string, out long)"/>
    /// over the DLL's MonoScript sub-assets) — never re-derived via the community MD4-hash
    /// recipe. Asking the editor is exact; re-implementing its hash is a correctness risk.</para>
    ///
    /// <para>release_free_package.ps1 makes a fresh map from every free release's DLLs and
    /// release_upgrade_package.ps1 puts it in the upgrade repo, so the map always matches the
    /// newest free version (PLAN §6).</para>
    /// </summary>
    public static class UpgradeMapGenerator
    {
        // The free package's precompiled assemblies whose script identities user assets reference.
        // Matched by file name (without extension) anywhere in the project.
        private static readonly string[] FreeDllNames = { "SFXSystem.Runtime", "SFXSystem.Editor" };

        private const string OutputFileName = "UpgradeMap.json";

        // ---- Map schema (PLAN §2). Plain serializable classes for JsonUtility. ----------------

        [Serializable]
        public class TypeEntry
        {
            public long fileId;   // MonoScript local file identifier inside the DLL
            public string type;   // fully-qualified type name (namespace + name)
        }

        [Serializable]
        public class DllEntry
        {
            public string assembly;                          // e.g. "SFXSystem.Runtime"
            public string guid;                              // the DLL asset's meta GUID
            public List<TypeEntry> types = new List<TypeEntry>();
        }

        [Serializable]
        public class UpgradeMap
        {
            public string version;      // free package version these identities correspond to
            public string generatedUtc; // audit stamp
            public List<DllEntry> dlls = new List<DllEntry>();
        }

        // ----------------------------------------------------------------------------------------

        [MenuItem("Tools/PepperSFX/Publishing/Generate Upgrade Map")]
        public static void Generate()
        {
            var map = new UpgradeMap
            {
                generatedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'"),
                version = "unknown"
            };

            int totalTypes = 0, unresolved = 0;
            var report = new System.Text.StringBuilder();

            foreach (string dllName in FreeDllNames)
            {
                string dllPath = FindDllPath(dllName);
                if (dllPath == null)
                {
                    report.AppendLine($"✗ {dllName}.dll — NOT FOUND in this project (skipped).");
                    continue;
                }

                string dllGuid = AssetDatabase.AssetPathToGUID(dllPath);
                if (string.IsNullOrEmpty(dllGuid))
                {
                    report.AppendLine($"✗ {dllName}.dll — found at '{dllPath}' but has no GUID (missing .meta?). Skipped.");
                    continue;
                }

                var entry = new DllEntry { assembly = dllName, guid = dllGuid };

                // Every serializable script class in the DLL surfaces as a MonoScript sub-asset.
                foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(dllPath))
                {
                    var script = obj as MonoScript;
                    if (script == null) continue;

                    if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(script, out string guid, out long localId))
                    {
                        unresolved++;
                        report.AppendLine($"  ⚠ {dllName}: could not resolve local id for sub-asset '{script.name}' — NOT in map.");
                        continue;
                    }

                    // Full type name is the join key the upgrade window matches source scripts on.
                    // GetClass() can be null for sub-assets whose type Unity can't bind (e.g. the
                    // defining assembly failed to load) — those can't be matched later either, so
                    // they are skipped loudly rather than recorded half-known.
                    var cls = script.GetClass();
                    if (cls == null)
                    {
                        unresolved++;
                        report.AppendLine($"  ⚠ {dllName}: sub-asset '{script.name}' has no resolvable class — NOT in map.");
                        continue;
                    }

                    entry.types.Add(new TypeEntry { fileId = localId, type = cls.FullName });
                    totalTypes++;
                }

                entry.types.Sort((a, b) => string.CompareOrdinal(a.type, b.type)); // stable diffs
                map.dlls.Add(entry);
                report.AppendLine($"✓ {dllName}.dll — guid {dllGuid}, {entry.types.Count} type identities ({dllPath}).");

                // Version: nearest package.json above the DLL (the dist package root), best-effort.
                if (map.version == "unknown")
                {
                    string v = TryReadPackageVersionAbove(dllPath);
                    if (v != null) map.version = v;
                }
            }

            if (totalTypes == 0)
            {
                EditorUtility.DisplayDialog("Generate Upgrade Map",
                    "No type identities were captured — are the free package DLLs " +
                    $"({string.Join(", ", FreeDllNames)}) present in this project?\n\n" + report,
                    "OK");
                return;
            }

            string outPath = OutputPath();
            File.WriteAllText(outPath, JsonUtility.ToJson(map, prettyPrint: true));
            AssetDatabase.Refresh();

            string summary = $"UpgradeMap.json written: {map.dlls.Count} DLL(s), {totalTypes} type identities, " +
                             $"version '{map.version}', {unresolved} unresolved (see console).\n{outPath}";
            Debug.Log($"[UpgradeMapGenerator]\n{summary}\n{report}");
            EditorUtility.DisplayDialog("Generate Upgrade Map", summary +
                (unresolved > 0 ? $"\n\n⚠ {unresolved} sub-asset(s) could not be resolved — check the console before shipping." : ""),
                "OK");
        }

        // Locate a DLL by file name anywhere in the project (Assets/ or an embedded package).
        private static string FindDllPath(string dllName)
        {
            foreach (string guid in AssetDatabase.FindAssets(dllName))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileName(path).Equals(dllName + ".dll", StringComparison.OrdinalIgnoreCase))
                    return path;
            }
            return null;
        }

        // Walk up from the DLL looking for the dist package's package.json; return its "version".
        private static string TryReadPackageVersionAbove(string assetPath)
        {
            try
            {
                var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(assetPath)) ?? ".");
                for (int depth = 0; dir != null && depth < 6; depth++, dir = dir.Parent)
                {
                    string pkg = Path.Combine(dir.FullName, "package.json");
                    if (!File.Exists(pkg)) continue;
                    var m = Regex.Match(File.ReadAllText(pkg), "\"version\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) return m.Groups[1].Value;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UpgradeMapGenerator] Could not read package.json version: {e.Message}");
            }
            return null;
        }

        // Write next to this script (the release test project copies it into Assets/UpdateToPaid/); fall back to Assets/.
        private static string OutputPath()
        {
            foreach (string guid in AssetDatabase.FindAssets($"t:MonoScript {nameof(UpgradeMapGenerator)}"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == nameof(UpgradeMapGenerator))
                    return Path.Combine(Path.GetDirectoryName(path) ?? "Assets", OutputFileName);
            }
            return Path.Combine("Assets", OutputFileName);
        }
    }
}
#endif
