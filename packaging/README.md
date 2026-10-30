# Windows EXE packaging

The installer contains the desktop controller, the reviewed NAudio 1.10.0 DLL with its Ms-PL license, configuration examples, runtime/bootstrap source, and user documentation. It never packages the developer's configuration, notes, recordings, logs, `work`, Python environment, models, or FFmpeg. New files require an explicit addition to `Build-Installer.ps1`.

The per-user default is `%LOCALAPPDATA%\Programs\verbit`. First launch opens settings without needing Python. Quick notes, native recording, MP3 export and optional API transcription need no Python or model. Recording-only is the default. There is no recommended-model download entry or installer task. Users may explicitly configure their own compatible audio API; existing models and configurations are retained. See [installation instructions](../documentation/install-windows.md). Setup checks .NET Framework 4.8 and requires Windows 10 1809 / Windows 11 x64.

Build from a reviewed checkout using Windows PowerShell 5.1 or newer:

Source/runtime inputs live in `src/`, tests in `tests/`. Restore the reviewed NAudio DLL to `src/lib/NAudio.dll` using `src/Install.ps1` before packaging. Package paths remain flat for existing installations; the builder never falls back to ignored legacy copies at the repository root.

```powershell
# Optional, explicit download of the pinned official compiler. Downloads only
# to validation, checks SHA256 plus Authenticode, then extracts portable files.
powershell -NoProfile -ExecutionPolicy Bypass -File .\packaging\Get-InnoCompiler.ps1

powershell -NoProfile -ExecutionPolicy Bypass -File .\packaging\Build-Installer.ps1
```

Alternatively supply `-CompilerPath 'C:\...\ISCC.exe'`. The build stages a new allowlisted directory, builds from that staged source, writes a SHA256 file manifest, and produces `validation\release\verbit-0.1.0-beta.36-setup-x64.exe` plus `SHA256SUMS.txt`. It does not replace a running local controller. Builds are repeatable through the script; byte-for-byte determinism is not claimed. These beta artifacts have no Authenticode signing certificate.

The pinned compiler is [Inno Setup 6.7.3 from its official release](https://github.com/jrsoftware/issrc/releases/tag/is-6_7_3), distributed by Pyrsys B.V.; [official verification and portable-mode documentation](https://jrsoftware.org/isdl-verify.php), [portable mode](https://jrsoftware.org/ishelp/topic_technotes.htm). Inno Setup's own license and applicable commercial-use terms remain in effect for the build tool. The compiler is not included in the product payload.

Run the installer smoke test only in a Windows user profile without an existing EXE installer registration:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\packaging\Test-Installer.ps1 `
  -InstallerPath .\validation\release\verbit-0.1.0-beta.36-setup-x64.exe
```

This test uses a unique `validation\installer-smoke-*\app` directory, disables application launch and shortcut tasks, and checks clean install, cross-directory installation rejection, old-uninstaller ownership rejection, occupied EXE and worker-byte-lock protection, active/unknown session rejection, upgrade preservation, native configuration validation, and uninstall preservation using synthetic fixtures. It writes an actual temporary HKCU uninstall entry and removes it on successful uninstall; it never replaces an existing registered install. It does not access the clipboard, record/play audio, or download models. A failed run leaves its evidence and isolated install for inspection.

For an actual Windows guest with no Python, models or developer tools, follow [clean Windows validation](../documentation/clean-windows-validation.md). `Prepare-CleanWindows.ps1 -InstallerPath <EXE>` stages only the explicit installer and test helpers by default; it emits a 4 GiB `.wsb` with networking and clipboard/microphone redirection disabled. It does not enable Windows features or launch the guest. `Test-CleanWindowsGuest.ps1` refuses a normal user account; guest reports, not preparation success, determine acceptance. Historical local-model research requires the explicit `-LegacyLocalTranscription` switch and two synthetic speech WAVs; it is not a first-release installation step.

Silent operations for automation:

```powershell
# /TASKS="" disables optional desktop and startup shortcuts. /NOICONS also
# skips the Start menu entries. Normal interactive installation creates them.
.\verbit-0.1.0-beta.36-setup-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /TASKS="" /DIR="C:\Users\NAME\AppData\Local\Programs\verbit"
& 'C:\Users\NAME\AppData\Local\Programs\verbit\unins000.exe' /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

An existing registered install can be upgraded only at its original directory; uninstall it normally before choosing another location. An old uninstaller cannot remove another directory's current registration. Upgrade/uninstall refuse to change an occupied or inaccessible controller or Python executable, a held `processing.lock` byte-zero lock, or active/unknown session states. Stale unheld lock files and completed/error states do not block updates. Resolve interrupted meetings through the existing installation's recovery flow before retrying; do not erase raw audio or rewrite states to bypass the check. No process is force-closed.

Uninstall removes only installer-owned files and shortcuts. It retains `config.json`, `.venv`, `models`, `work`, and output recordings/notes. There is no recursive deletion of application data. Review retained folders manually after confirming that no records or recovery data are needed. `Check-InstallSafety.ps1` uses only Windows PowerShell and the .NET runtime, reads state/lock metadata, and does not access note or audio content. Its source is ASCII so Windows PowerShell 5.1 does not depend on locale-sensitive script decoding.
