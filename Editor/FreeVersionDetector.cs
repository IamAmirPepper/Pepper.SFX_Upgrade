#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AudioSystem.UpdateToPaid.Editor
{
    /// <summary>
    /// Install-time guide (Slice 4, docs/UpdateToPaid/PLAN.md §4). This package is installed next to
    /// the FREE package (precompiled DLLs), so the usual moment this fires is right after installing
    /// it: it walks the user through removing free, importing full (source) and running the window.
    /// It fires again if free is still there once full is imported (the two collide on assembly
    /// names). Instructs only; never deletes anything. Once per editor session, permanently
    /// suppressible per project.
    ///
    /// <para>Best-effort by nature: if a duplicate-assembly error blocks this assembly from
    /// compiling too, the dialog can't show; the README's steps remain the fallback.</para>
    /// </summary>
    [InitializeOnLoad]
    internal static class FreeVersionDetector
    {
        private const string SessionKey = "SFXSystem.UpdateToPaid.PromptShown";
        private const string SuppressKey = "SFXSystem.UpdateToPaid.SuppressPrompt";

        static FreeVersionDetector()
        {
            // Deferred: AssetDatabase isn't reliable during static construction on domain reload.
            EditorApplication.delayCall += Check;
        }

        private static void Check()
        {
            if (SessionState.GetBool(SessionKey, false)) return;
            SessionState.SetBool(SessionKey, true);
            if (EditorPrefs.GetBool(ProjectScopedSuppressKey(), false)) return;

            if (!UpgradeFromFreeWindow.TryLoadMap(out var map, out _, out _)) return;

            string foundPath = null;
            foreach (var dll in map.dlls)
            {
                // Same stale-mapping guard as the window's preflight: GUIDToAssetPath keeps
                // returning a removed asset's old path, so confirm the asset actually loads.
                string p = AssetDatabase.GUIDToAssetPath(dll.guid);
                if (!string.IsNullOrEmpty(p) && AssetDatabase.LoadMainAssetAtPath(p) != null) { foundPath = p; break; }
            }
            if (foundPath == null) return; // clean project (or already migrated) — stay silent

            int choice = EditorUtility.DisplayDialogComplex(
                "PepperSFX — free version detected",
                "The FREE package's compiled DLLs are in this project:\n\n" +
                $"    {foundPath}\n\n" +
                "Free and full can't be in one project (same assembly names). To move to full:\n\n" +
                "1.  Back up your project, and have the full version downloaded.\n" +
                "2.  Remove the free package (Package Manager). Your audio assets will show\n" +
                "     'Missing (Mono Script)': that's EXPECTED and recoverable.\n" +
                "3.  Do NOT save scenes or assets in that state.\n" +
                "4.  Import the full version.\n" +
                "5.  Run  Window ▸ PepperSFX ▸ Setup ▸ Upgrade From Free  to restore every reference.",
                "Open Upgrade Window", "Later", "Don't ask again (this project)");

            if (choice == 0) UpgradeFromFreeWindow.Open();
            else if (choice == 2) EditorPrefs.SetBool(ProjectScopedSuppressKey(), true);
        }

        // EditorPrefs are machine-global; scope the suppression to this project by path hash.
        private static string ProjectScopedSuppressKey()
            => SuppressKey + "." + Application.dataPath.GetHashCode().ToString("X8");
    }
}
#endif
