#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace AudioSystem.UpdateToPaid.Editor
{
    /// <summary>
    /// USER-FACING migration tool (Slices 2–3, docs/UpdateToPaid/PLAN.md §3). Restores every serialized
    /// reference that pointed at the FREE package's precompiled DLLs so it points at the PAID
    /// package's source scripts instead — events, containers, buses, scenes, prefabs all come back.
    ///
    /// <para>Strictly ordered: Preflight → Dry Run (always, no opt-out) → Apply (zip backup +
    /// typed confirmation, F1) → Verify. The rewrite matches each <c>{fileID, guid}</c> PAIR in one
    /// pattern — never the id or guid independently — so an unrelated asset can't be corrupted.
    /// Idempotent: a second Apply finds nothing to do.</para>
    ///
    /// <para>This assembly deliberately references nothing of SFXSystem (PLAN F6): it must compile
    /// and run while the project is mid-migration with dangling script references everywhere. It
    /// ships in its own package (com.pepper.sfx.upgrade), installed next to free, so removing free
    /// doesn't remove it; neither free nor full contains it.</para>
    /// </summary>
    public sealed class UpgradeFromFreeWindow : EditorWindow
    {
        private const string ConfirmToken = "UPGRADE";

        // ---- Map / resolution state ------------------------------------------------------------
        private UpgradeMapGenerator.UpgradeMap _map;
        private string _mapPath, _mapError;

        // One rewrite unit per shipped DLL: its guid-anchored regex + fileId → source-script guid.
        private sealed class DllRewrite
        {
            public string Assembly;
            public string Guid;
            public Regex Pattern;
            public readonly Dictionary<long, string> IdToSourceGuid = new Dictionary<long, string>();
            public int MappedTypes, UnmatchedTypes;
        }
        private readonly List<DllRewrite> _rewrites = new List<DllRewrite>();
        private readonly List<string> _unmatched = new List<string>();   // F3: in map, no source counterpart
        private readonly List<string> _duplicates = new List<string>();  // same FullName on two source scripts

        // ---- Preflight state -------------------------------------------------------------------
        private struct Check { public bool Ok; public string Label; }
        private readonly List<Check> _preflight = new List<Check>();
        private bool _preflightRan, _preflightOk;

        // ---- Scan / apply state ----------------------------------------------------------------
        private sealed class FileHit { public string Path; public int Rewrites; public int UnknownIds; }
        private readonly List<FileHit> _hits = new List<FileHit>();
        private readonly List<string> _asmdefUsers = new List<string>(); // F5: user asmdefs on the DLLs
        private int _totalUnknownIds, _strippedRefs, _filesScanned;
        private bool _dryRunDone, _applied, _scanCancelled;
        private string _currentFile;

        // GUI counts are ALWAYS derived from the hit list of the same pass, so the report can
        // never show contradictory numbers (e.g. "6 references in 0 files") after a partial pass.
        private int HitRewriteTotal { get { int n = 0; foreach (var h in _hits) n += h.Rewrites; return n; } }
        private string _confirmText = "";
        private string _backupZip, _applyError, _verifyResult;
        private Vector2 _scroll;

        [MenuItem("Window/PepperSFX/Setup/Upgrade From Free", priority = 1047)]
        public static void Open()
        {
            var w = GetWindow<UpgradeFromFreeWindow>("Upgrade From Free");
            w.minSize = new Vector2(560, 480);
        }

        // =========================================================================================
        //  Map loading (shared with FreeVersionDetector)
        // =========================================================================================

        /// <summary>Locate and parse UpgradeMap.json anywhere in the project. False + error if absent/invalid.</summary>
        internal static bool TryLoadMap(out UpgradeMapGenerator.UpgradeMap map, out string path, out string error)
        {
            map = null; path = null; error = null;
            foreach (string guid in AssetDatabase.FindAssets("UpgradeMap"))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (!Path.GetFileName(p).Equals("UpgradeMap.json", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    map = JsonUtility.FromJson<UpgradeMapGenerator.UpgradeMap>(File.ReadAllText(p));
                    if (map == null || map.dlls == null || map.dlls.Count == 0)
                    { error = $"'{p}' parsed but contains no DLL entries."; map = null; return false; }
                    path = p;
                    return true;
                }
                catch (Exception e) { error = $"Failed to parse '{p}': {e.Message}"; return false; }
            }
            error = "UpgradeMap.json not found in the project. It comes with the Pepper.SFX Upgrade From Free package: reinstall that package.";
            return false;
        }

        // =========================================================================================
        //  Step 1 — Preflight (PLAN §3.1)
        // =========================================================================================

        private void RunPreflight()
        {
            _preflight.Clear();
            _rewrites.Clear(); _unmatched.Clear(); _duplicates.Clear();
            _dryRunDone = _applied = false; _hits.Clear(); _asmdefUsers.Clear();
            _verifyResult = null; _applyError = null;

            // 1. Map present + parseable.
            bool mapOk = TryLoadMap(out _map, out _mapPath, out _mapError);
            _preflight.Add(new Check { Ok = mapOk, Label = mapOk
                ? $"UpgradeMap.json loaded (free v{_map.version}, {_map.dlls.Count} DLLs) — {_mapPath}"
                : $"UpgradeMap.json: {_mapError}" });
            if (!mapOk) { _preflightRan = true; _preflightOk = false; return; }

            // 2. Text serialization — the rewrite is text-only, hard requirement.
            bool textMode = EditorSettings.serializationMode == SerializationMode.ForceText;
            _preflight.Add(new Check { Ok = textMode, Label = textMode
                ? "Asset serialization is Force Text."
                : "Asset serialization is NOT Force Text — enable it: Edit ▸ Project Settings ▸ Editor ▸ Asset Serialization = Force Text, then re-run." });

            // 3. Free DLLs must be gone (identical assembly names cannot coexist with the source).
            bool dllsGone = true;
            foreach (var dll in _map.dlls)
            {
                // GUIDToAssetPath returns the LAST-KNOWN path even after the asset/package is
                // removed (the guid→path table is not purged), so the mapping alone is not proof
                // of presence — confirm the asset actually loads before failing the check.
                string p = AssetDatabase.GUIDToAssetPath(dll.guid);
                if (!string.IsNullOrEmpty(p) && AssetDatabase.LoadMainAssetAtPath(p) != null)
                {
                    dllsGone = false;
                    _preflight.Add(new Check { Ok = false, Label = $"Free DLL still present: '{p}' — remove the free package first." });
                }
            }
            if (dllsGone) _preflight.Add(new Check { Ok = true, Label = "Free package DLLs are removed." });

            // 4. Paid source present: resolve every map type to its source script's meta GUID.
            var typeToGuid = BuildSourceTypeIndex(_map);
            foreach (var dll in _map.dlls)
            {
                var rw = new DllRewrite
                {
                    Assembly = dll.assembly,
                    Guid = dll.guid,
                    // Unity writes: {fileID: -123, guid: abc…, type: 3}. Tolerant whitespace; the id
                    // and the guid are captured together so replacements can never mismatch (PLAN §3).
                    Pattern = new Regex(@"\{fileID:\s*(-?\d+),\s*guid:\s*" + Regex.Escape(dll.guid) + @"\s*,\s*type:\s*3\}",
                                        RegexOptions.Compiled),
                };
                foreach (var t in dll.types)
                {
                    if (typeToGuid.TryGetValue(t.type, out string srcGuid)) { rw.IdToSourceGuid[t.fileId] = srcGuid; rw.MappedTypes++; }
                    else { rw.UnmatchedTypes++; _unmatched.Add($"{t.type}  ({dll.assembly})"); }
                }
                _rewrites.Add(rw);
            }
            int mapped = 0; foreach (var r in _rewrites) mapped += r.MappedTypes;
            bool sourceOk = mapped > 0 && typeToGuid.ContainsKey("AudioSystem.AudioManager");
            _preflight.Add(new Check { Ok = sourceOk, Label = sourceOk
                ? $"Paid source scripts resolved: {mapped} type identities mapped."
                : "Full version's source scripts not found (AudioSystem.AudioManager unresolvable). Import the full version and let it compile, then re-run." });
            if (_unmatched.Count > 0)
                _preflight.Add(new Check { Ok = true, Label = $"⚠ {_unmatched.Count} map type(s) have no source counterpart — they will be REPORTED, never rewritten (F3). See dry run." });
            if (_duplicates.Count > 0)
                _preflight.Add(new Check { Ok = true, Label = $"⚠ {_duplicates.Count} type name(s) matched MULTIPLE source scripts — excluded from rewrite (F3): {string.Join(", ", _duplicates)}" });

            _preflightOk = mapOk && textMode && dllsGone && sourceOk;
            _preflightRan = true;
        }

        // FullName → source .cs meta GUID, restricted to the paid assemblies named in the map.
        private Dictionary<string, string> BuildSourceTypeIndex(UpgradeMapGenerator.UpgradeMap map)
        {
            var allowed = new HashSet<string>();
            foreach (var d in map.dlls) allowed.Add(d.assembly);

            var index = new Dictionary<string, string>();
            var dupes = new HashSet<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:MonoScript"))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(p);
                var cls = script != null ? script.GetClass() : null;
                if (cls == null) continue;
                if (!allowed.Contains(cls.Assembly.GetName().Name)) continue;

                string full = cls.FullName;
                if (index.ContainsKey(full)) { dupes.Add(full); continue; }
                index[full] = guid;
            }
            // A duplicated name is ambiguous → conservative: exclude entirely (F3-style: never guess).
            foreach (var d in dupes) { index.Remove(d); _duplicates.Add(d); }
            return index;
        }

        // =========================================================================================
        //  Step 2/3 — Scan (dry run) and Apply share one pass (PLAN §3.2–3.3)
        // =========================================================================================

        // F2: Assets/ + embedded local packages (physical folders under Packages/), never the cache.
        private static List<string> CandidateFiles(string extensionFilter = null)
        {
            var files = new List<string>();
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            var roots = new List<string> { Application.dataPath };
            string packagesDir = Path.Combine(projectRoot ?? ".", "Packages");
            if (Directory.Exists(packagesDir))
                foreach (var d in Directory.GetDirectories(packagesDir)) roots.Add(d);

            string[] exts = extensionFilter != null ? new[] { extensionFilter } : new[] { ".unity", ".prefab", ".asset" };
            foreach (string root in roots)
                foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    foreach (string ext in exts)
                        if (f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { files.Add(f); break; }
            return files;
        }

        private static readonly Regex StrippedRef = new Regex(@"m_Script:\s*\{fileID:\s*0\}", RegexOptions.Compiled);

        private void Scan(bool write)
        {
            _hits.Clear(); _asmdefUsers.Clear();
            _totalUnknownIds = _strippedRefs = _filesScanned = 0;
            _applyError = null; _scanCancelled = false; _currentFile = null;

            var files = CandidateFiles();
            var toBackup = new List<string>();
            try
            {
                // Pass 1 (dry) / single pass (apply): find + rewrite in memory, collect hits.
                var pending = new List<(string path, string newText, bool hadBom)>();
                for (int i = 0; i < files.Count; i++)
                {
                    if (i % 50 == 0 && EditorUtility.DisplayCancelableProgressBar(
                            write ? "Upgrade From Free — applying" : "Upgrade From Free — dry run",
                            files[i], (float)i / files.Count))
                        throw new OperationCanceledException("Cancelled by user.");

                    _currentFile = files[i];
                    string text = ReadText(files[i], out bool hadBom);
                    _filesScanned++;

                    // Stripped refs are counted on every file (they no longer carry our guid at all).
                    _strippedRefs += StrippedRef.Matches(text).Count;

                    // Cheap reject before regex: none of the DLL guids appear.
                    bool candidate = false;
                    foreach (var rw in _rewrites) if (text.Contains(rw.Guid)) { candidate = true; break; }
                    if (!candidate) continue;

                    int fileRewrites = 0, fileUnknown = 0;
                    string newText = text;
                    foreach (var rw in _rewrites)
                    {
                        newText = rw.Pattern.Replace(newText, m =>
                        {
                            long id = long.Parse(m.Groups[1].Value);
                            if (rw.IdToSourceGuid.TryGetValue(id, out string srcGuid))
                            { fileRewrites++; return $"{{fileID: 11500000, guid: {srcGuid}, type: 3}}"; }
                            fileUnknown++;            // references the DLL but the type has no source home (F3)
                            return m.Value;           // leave untouched — visible is fixable, guessed is corrupted
                        });
                    }

                    if (fileRewrites == 0 && fileUnknown == 0) continue;
                    _hits.Add(new FileHit { Path = ToRelative(files[i]), Rewrites = fileRewrites, UnknownIds = fileUnknown });
                    _totalUnknownIds += fileUnknown;

                    if (write && fileRewrites > 0)
                    {
                        toBackup.Add(files[i]);
                        pending.Add((files[i], newText, hadBom));
                    }
                }

                if (write)
                {
                    // F1: zip the untouched originals BEFORE the first write.
                    _backupZip = WriteBackupZip(toBackup);
                    foreach (var (path, newText, hadBom) in pending)
                        File.WriteAllText(path, newText, new UTF8Encoding(hadBom));
                    AssetDatabase.Refresh();
                    _applied = true;
                }

                // F5: user asmdefs that precompiled-reference the free DLLs — report only, never edit.
                foreach (string asmdef in CandidateFiles(".asmdef"))
                {
                    _currentFile = asmdef;
                    string t = File.ReadAllText(asmdef);
                    foreach (var rw in _rewrites)
                        if (t.Contains(rw.Assembly + ".dll")) { _asmdefUsers.Add(ToRelative(asmdef)); break; }
                }

                _dryRunDone = true;
            }
            catch (OperationCanceledException) { _scanCancelled = true; /* partial report shown as such */ }
            catch (Exception e)
            {
                _applyError = $"{e.GetType().Name}: {e.Message} (while processing '{_currentFile ?? "n/a"}')";
                Debug.LogError($"[UpgradeFromFree] {(write ? "Apply" : "Dry run")} failed on '{_currentFile}': {e}");
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        // =========================================================================================
        //  Step 4 — Verify (PLAN §3.4): success = zero surviving DLL-guid references.
        // =========================================================================================

        private void Verify()
        {
            int survivors = 0; var where = new List<string>();
            var files = CandidateFiles();
            try
            {
                for (int i = 0; i < files.Count; i++)
                {
                    if (i % 100 == 0) EditorUtility.DisplayProgressBar("Upgrade From Free — verifying", files[i], (float)i / files.Count);
                    string text = ReadText(files[i], out _);
                    foreach (var rw in _rewrites)
                    {
                        int c = rw.Pattern.Matches(text).Count;
                        if (c > 0) { survivors += c; if (where.Count < 10) where.Add($"{ToRelative(files[i])} ({c})"); }
                    }
                }
            }
            finally { EditorUtility.ClearProgressBar(); }

            _verifyResult = survivors == 0
                ? "✓ Verified: no references to the free DLLs remain. Migration complete."
                : $"✗ {survivors} reference(s) still point at the free DLLs" +
                  (_totalUnknownIds > 0 ? " (expected if types were reported as unmatched — those are left for manual review)" : "") +
                  ":\n  " + string.Join("\n  ", where);
        }

        // =========================================================================================
        //  Helpers
        // =========================================================================================

        private static string ReadText(string path, out bool hadBom)
        {
            var bytes = File.ReadAllBytes(path);
            hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            return Encoding.UTF8.GetString(bytes, hadBom ? 3 : 0, bytes.Length - (hadBom ? 3 : 0));
        }

        private static string ToRelative(string full)
        {
            string root = Path.GetDirectoryName(Application.dataPath) ?? "";
            full = Path.GetFullPath(full);
            return full.StartsWith(root) ? full.Substring(root.Length + 1).Replace('\\', '/') : full;
        }

        // F1: zip originals to <project>/Backups/UpgradeFromFree_<stamp>.zip (outside Assets — never imports).
        private static string WriteBackupZip(List<string> files)
        {
            if (files.Count == 0) return null;
            string root = Path.GetDirectoryName(Application.dataPath) ?? ".";
            string dir = Path.Combine(root, "Backups");
            Directory.CreateDirectory(dir);
            string zipPath = Path.Combine(dir, $"UpgradeFromFree_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
            using (var fs = new FileStream(zipPath, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (string f in files)
                {
                    var entry = zip.CreateEntry(ToRelative(f), System.IO.Compression.CompressionLevel.Optimal);
                    using var es = entry.Open();
                    var bytes = File.ReadAllBytes(f);
                    es.Write(bytes, 0, bytes.Length);
                }
            }
            return zipPath;
        }

        // =========================================================================================
        //  GUI
        // =========================================================================================

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.HelpBox(
                "Restores your audio assets after switching from the FREE package (compiled DLLs) to the " +
                "FULL version (source). Order: Preflight → Dry Run → Apply → Verify. Nothing is " +
                "written until Apply, and Apply zips every file it touches into Backups/ first.",
                MessageType.Info);

            // ---- 1. Preflight ----
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("1 — Preflight", EditorStyles.boldLabel);
            if (GUILayout.Button("Run Preflight", GUILayout.Height(24))) RunPreflight();
            if (_preflightRan)
            {
                foreach (var c in _preflight)
                    EditorGUILayout.HelpBox((c.Ok ? "✓  " : "✗  ") + c.Label, c.Ok ? MessageType.None : MessageType.Error);
                if (!_preflightOk)
                    EditorGUILayout.HelpBox("Fix the items above, then re-run Preflight.", MessageType.Warning);
            }

            // ---- 2. Dry run ----
            using (new EditorGUI.DisabledScope(!_preflightRan || !_preflightOk))
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("2 — Dry Run (nothing is written)", EditorStyles.boldLabel);
                if (GUILayout.Button("Scan Project (Dry Run)", GUILayout.Height(24))) Scan(write: false);
            }
            if (_dryRunDone) DrawReport();

            // ---- 3. Apply ----
            using (new EditorGUI.DisabledScope(!_dryRunDone || _applied || HitRewriteTotal == 0))
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("3 — Apply", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    $"Will rewrite {HitRewriteTotal} reference(s) across {_hits.Count} file(s). A zip of the " +
                    "originals is written to Backups/ first. Commit or back up your project before applying. " +
                    $"Type {ConfirmToken} to enable the button.", MessageType.Warning);
                _confirmText = EditorGUILayout.TextField("Confirmation", _confirmText);
                using (new EditorGUI.DisabledScope(_confirmText != ConfirmToken))
                {
                    if (GUILayout.Button($"Apply — rewrite {HitRewriteTotal} reference(s)", GUILayout.Height(28)))
                        if (EditorUtility.DisplayDialog("Upgrade From Free",
                                $"Rewrite {HitRewriteTotal} reference(s) in {_hits.Count} file(s)?\n\nOriginals are zipped to Backups/ first.",
                                "Apply", "Cancel"))
                            Scan(write: true);
                }
            }
            if (_applied)
            {
                EditorGUILayout.HelpBox($"✓ Applied. Backup: {_backupZip}\n" +
                    "If a scene was open during Apply, reopen it (File ▸ Open Scene) so the editor reloads the fixed references.",
                    MessageType.Info);
            }
            if (_applyError != null) EditorGUILayout.HelpBox($"Failed: {_applyError} — nothing partial was refreshed; restore from Backups/ if needed.", MessageType.Error);

            // ---- 4. Verify ----
            using (new EditorGUI.DisabledScope(!_applied))
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("4 — Verify", EditorStyles.boldLabel);
                if (GUILayout.Button("Verify (re-scan for surviving references)", GUILayout.Height(24))) Verify();
            }
            if (_verifyResult != null)
                EditorGUILayout.HelpBox(_verifyResult, _verifyResult.StartsWith("✓") ? MessageType.Info : MessageType.Error);

            EditorGUILayout.EndScrollView();
        }

        private void DrawReport()
        {
            EditorGUILayout.Space(2);
            if (_scanCancelled)
                EditorGUILayout.HelpBox("Scan was CANCELLED — this report is partial. Re-run the dry run before applying.", MessageType.Warning);
            EditorGUILayout.HelpBox(
                $"Scanned {_filesScanned} file(s): {HitRewriteTotal} rewritable reference(s) in {_hits.Count} file(s)." +
                (_totalUnknownIds > 0 ? $"\n⚠ {_totalUnknownIds} reference(s) to types with no source counterpart — left untouched (F3)." : "") +
                (_strippedRefs > 0 ? $"\n⚠ {_strippedRefs} already-stripped reference(s) (m_Script fileID 0) — an asset was re-saved while missing; not recoverable by this tool." : ""),
                HitRewriteTotal > 0 ? MessageType.Info : MessageType.Warning);

            foreach (var h in _hits)
                EditorGUILayout.LabelField($"  {h.Path}   —   {h.Rewrites} rewrite(s)" + (h.UnknownIds > 0 ? $", {h.UnknownIds} unmatched" : ""),
                                           EditorStyles.miniLabel);

            if (_unmatched.Count > 0)
            {
                EditorGUILayout.LabelField($"Unmatched map types ({_unmatched.Count}):", EditorStyles.miniBoldLabel);
                foreach (var t in _unmatched) EditorGUILayout.LabelField("  " + t, EditorStyles.miniLabel);
            }
            if (_asmdefUsers.Count > 0)
                EditorGUILayout.HelpBox(
                    "Your own asmdef(s) reference the free DLLs under precompiledReferences — switch them to " +
                    "assembly-definition references manually (this tool never edits your code):\n  " +
                    string.Join("\n  ", _asmdefUsers), MessageType.Warning);
        }
    }
}
#endif
