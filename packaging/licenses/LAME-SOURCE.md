# LAME 3.100

Verjot dynamically loads the unmodified `libmp3lame.32.dll` / `libmp3lame.64.dll`
from NAudio.Lame 1.1.1's official NuGet package. The NAudio.Lame managed wrapper
is not used or distributed. The encoder is LAME 3.100, copyright the LAME
Project and contributors, under GNU Library General Public License version 2
or, where the individual source notices permit, a later version. See LAME.txt
and the source archive's COPYING, LICENSE and individual file notices.

- Project: https://lame.sourceforge.io/
- Binary package: https://api.nuget.org/v3-flatcontainer/naudio.lame/1.1.1/naudio.lame.1.1.1.nupkg
- 32-bit DLL SHA256: `91d3c0cc833265066c51408d940faaf76c5b8fc5bda854540a9cc6c88b1d1f20`
- 64-bit DLL SHA256: `71dba147b6c16d8d0f012b8e657d52e12cb8796a659e1151200d0a6c8b04bce3`
- Complete upstream source: `LAME-3.100-source.tar.gz`, distributed alongside
  this notice in Windows installers. Restored from Debian's unchanged upstream
  archive at https://deb.debian.org/debian/pool/main/l/lame/lame_3.100.orig.tar.gz
- Source archive SHA256: `ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e`

The source contains Windows build projects in `vc_solution/`, as well as the
upstream INSTALL instructions. Verjot does not modify the library. The pinned
build/download hashes verify the distributed files; compatible replacement
libraries can be loaded at runtime by replacing the appropriate DLL while the
application is closed. Debugging such modifications and reverse engineering
for that purpose are permitted. Verjot's application source remains MIT.

Source builds of Verjot can restore the archive using
`src/Restore-Encoder.ps1 -IncludeSource`. Preserve the archive and notices when
redistributing the native DLLs. Do not silently package only a binary download
URL in place of the accompanying source.
