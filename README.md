# Pepper.SFX: Upgrade From Free

Moves a Unity project from the **free version** of [Pepper.SFX](https://peppersfx.com) to the **full version**
without losing anything you made in free.

The free version ships as compiled DLLs; the full version is C# source. Unity tracks your assets' script references
by *asset identity*, so swapping the DLLs for source makes every Audio Event, container, bus and scene component show
**"Missing (Mono Script)"**. Your data is **not** lost: the references just point at an asset that no longer exists.
This package's **Upgrade From Free** window rewrites them all, once. It takes about five minutes.

It's its own package because it has to outlive free: you install it next to free, then remove free, and it's still
there to finish the job. Neither free nor full contains it. It's editor-only and never goes into a build.

---

## The one rule

> ⚠️ **While your assets show "Missing (Mono Script)", do NOT save them.**
> A missing reference is fully recoverable **until the asset is saved in that state**: saving strips it for good.
> Don't edit audio assets, don't Ctrl+S scenes, and say no to any "save scene?" prompt. Just follow the steps.

---

## Steps

**0. Back up.** Commit to version control, or copy the project folder. (The tool also zips every file it changes into
`Backups/` before writing, but back up anyway.) Have the full version downloaded from the Unity Asset Store.

**1. Install this package**, while free is still installed:
`Window ▸ Package Manager ▸ + ▸ Install package from git URL`, and paste

```
https://github.com/IamAmirPepper/Pepper.SFX_Upgrade.git
```

A dialog says the free version is in the project and lists these steps. That's this package; click **Later** and
carry on.

**2. Remove the free package.**
`Window ▸ Package Manager ▸ In Project ▸ Pepper's SFX System ▸ Remove`.
Your audio assets now show *Missing (Mono Script)*. **Expected.** Remember the rule above.

**3. Import the full version** from the Asset Store (`Window ▸ Package Manager ▸ My Assets ▸ Pepper.SFX ▸ Download ▸
Import`). If you import it *before* removing free, the Console shows "assembly already exists" errors: they go away
after step 2.

**4. Open the window:** `Window ▸ PepperSFX ▸ Setup ▸ Upgrade From Free`.

**5. Run Preflight.** Every check must pass:
   - *UpgradeMap.json loaded*: it comes with this package. The line names the free version it was made from.
   - *Force Text serialization*: Unity's default. If flagged, set `Edit ▸ Project Settings ▸ Editor ▸ Asset
     Serialization` to Force Text and run it again.
   - *Free DLLs removed*: if flagged, finish step 2.
   - *Source scripts resolved*: if flagged, the full version isn't imported or hasn't compiled yet.

**6. Scan Project (Dry Run).** Nothing is written. Read the report:
   - The file and reference counts should look right for the size of your project.
   - *"Unmatched types"* are classes that were in free but aren't in this full version. They're reported and left
     alone, never guessed at.
   - *"Already-stripped references"* means an asset was saved while missing (the rule above). The tool can't recover
     those: restore that asset from your backup or version control and run it again.

**7. Apply.** Type `UPGRADE`, click Apply, confirm. The originals are zipped to
`<project>/Backups/UpgradeFromFree_<date>.zip` first.

**8. Verify.** Click Verify. You want *"✓ no references to the free DLLs remain."*

**9. Reopen your scene** (`File ▸ Open Scene`) so the editor reloads the fixed references, then **press Play**.
Everything you made in free (events, containers, buses, zones, prefabs) works as before.

**10. Remove this package** (`Window ▸ Package Manager ▸ In Project ▸ Pepper.SFX Upgrade From Free ▸ Remove`). It's
only needed once. Keep the zip in `Backups/` until you're happy.

---

## Troubleshooting

| Problem | Fix |
|---|---|
| Preflight: *UpgradeMap.json not found* | This package didn't install completely. Remove it and install it again (step 1). |
| Preflight: *Free DLL still present* | The free package is still installed. Remove it in the Package Manager; deleting its files by hand isn't the same. |
| Preflight: *source scripts not found* | Import the full version (step 3) and wait for Unity to finish compiling. |
| Dry run finds 0 references but my assets are missing scripts | Either the missing scripts belong to something else, or the assets were saved while missing (see the *stripped* count). Restore them from your backup and run it again. |
| Report: *your asmdef references the free DLLs* | One of your own assembly definitions lists the DLL under `precompiledReferences`. Change it to an assembly-definition reference yourself: the tool never edits your code. |
| Something sounds wrong after upgrading | Restore the zip from `Backups/` (or revert in version control) and get in touch with the window's report and the Console log. |
| I need to undo everything | Unzip `Backups/UpgradeFromFree_<date>.zip` over the project folder, remove the full version and reinstall free. |

**Safe to run again:** after a successful upgrade, Dry Run and Apply simply find nothing to do.

---

## About

Made by Amir Pepper. The free and full versions, the guides and support: [peppersfx.com](https://peppersfx.com).
