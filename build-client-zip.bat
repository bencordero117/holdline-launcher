@echo off
setlocal EnableExtensions EnableDelayedExpansion

REM ============================================================================
REM  HOLDLINE - build the client bundle (Data.zip)        [rebuilt 17 Aug 2026]
REM ============================================================================
REM  Reproduces the verified-good release zip shape: 109 entries, top-level
REM  Data\ + Root\, the empty PluginsDev folder included, and a CLEAN settings
REM  template - never your live settings file, which carries your personal
REM  profileId and server IP.
REM
REM  Every path below is taken from ClientFiles.cs, not from memory.
REM ============================================================================
REM  WHAT THE 17 AUG REBUILD CHANGED, AND WHY
REM ============================================================================
REM  1. d3dx9_42.dll REMOVED FROM THE BUNDLE - PERMANENTLY. See the ban note
REM     below. This is the whole point of the rebuild.
REM
REM  2. Root\ still ships, because the launcher REQUIRES it. ClientFiles.cs
REM     line ~408: if FindRootFolder returns null the install aborts with
REM     "missing their Root folder". So Root\ now carries a plain README
REM     instead of the preloader. Every launcher already in the wild keeps
REM     working and nobody has to reinstall anything - MissingRequiredFiles
REM     (ClientFiles.cs ~244) only ever checks for MpClientPlugin.dll,
REM     SkyrimPlatform.dll, Data\Platform, and Plugins\skymp5-client.js. It has
REM     never checked the game root for anything at all. That was verified by
REM     reading it, not assumed.
REM
REM  3. The fingerprint block no longer runs an if/else inside PowerShell. The
REM     old one packed the whole comparison into a single PS line and cmd
REM     mangled its braces into "Missing closing '}' in statement block".
REM     Batch does the comparing now; PowerShell only ever returns one hash.
REM
REM  4. Only ONE hash gates the launcher: ClientBundleSha256 (ClientFiles.cs
REM     line ~137), the fingerprint of skymp5-client.js. The old script claimed
REM     to check three files against launcher constants, but only that one
REM     exists in ClientFiles.cs. The other two are printed as information.
REM ============================================================================
REM  d3dx9_42.dll IS BANNED - DO NOT PUT IT BACK
REM ============================================================================
REM  The previous version of this script REQUIRED it: refused to build without
REM  it, staged it into Root\, and failed verification if it was absent from the
REM  finished zip. Root\ installs beside SkyrimSE.exe on everyone who downloads.
REM
REM  It is the SKSE preloader for SSE Engine Fixes. On a machine without the real
REM  DirectX redistributable already present it REPLACES real DirectX with a
REM  stub, and Skyrim dies about four seconds into startup with no log, no popup
REM  and nothing to debug. It has already cost this project a full evening.
REM
REM  Whether Holdline needs Engine Fixes AT ALL is still an open question and
REM  must be answered before any part of it ships again. If it ever does, ship
REM  ONLY EngineFixes.dll, its .toml and _SNCT.ini - never the .pdb (30MB of
REM  debug symbols) and never the EngineFixes_preload.txt marker, whose absence
REM  makes it load as an ordinary SKSE plugin with all crash fixes intact and no
REM  popup.
REM
REM  If someone puts a DLL back into Root\, that is a regression.
REM ============================================================================
REM  HOW TO RUN
REM    Double-click it. If Skyrim lives elsewhere, pass the folder:
REM      build-client-zip.bat "E:\Games\Skyrim Special Edition"
REM
REM  It writes Data.zip next to this .bat and REFUSES to build a broken bundle -
REM  the finished zip is read back and checked before you are told it is done.
REM
REM  IF THE CLIENT HASH DIFFERS from ClientBundleSha256 that is NOT a fault. It
REM  means you patched the bundle, which is the normal case. But then the URL,
REM  ClientFilesVersion and ClientBundleSha256 must all be updated in the SAME
REM  edit, and the release published BEFORE shipping a launcher that points at
REM  it - otherwise every launcher re-downloads 180MB on every single join,
REM  forever. The script prints exactly what to paste.
REM ============================================================================

set "SKYRIM=%~1"
if "%SKYRIM%"=="" set "SKYRIM=D:\SteamLibrary\steamapps\common\Skyrim Special Edition"
if "%SKYRIM:~-1%"=="\" set "SKYRIM=%SKYRIM:~0,-1%"

set "OUTDIR=%~dp0"
if "%OUTDIR:~-1%"=="\" set "OUTDIR=%OUTDIR:~0,-1%"

set "STAGE=%OUTDIR%\holdline-zip-build"
set "ZIP=%OUTDIR%\Data.zip"
set "SDATA=%SKYRIM%\Data"

echo.
echo ==========================================================
echo  HOLDLINE client bundle builder
echo ==========================================================
echo  Skyrim : %SKYRIM%
echo  Output : %ZIP%
echo.

REM --- check the sources this build cannot live without -----------------------
if not exist "%SKYRIM%\" (
  echo [STOP] That Skyrim folder does not exist:
  echo        %SKYRIM%
  echo        Run again with the right folder in quotes, like:
  echo        build-client-zip.bat "E:\Games\Skyrim Special Edition"
  goto :fail
)
REM  NOTE THE \Plugins\. The real path has it. A version of this script that
REM  checked Data\Platform\skymp5-client.js stopped dead on a healthy install.
if not exist "%SDATA%\Platform\Plugins\skymp5-client.js" (
  echo [STOP] No skymp5-client.js at Data\Platform\Plugins.
  echo        That is the actual game code. Nothing to bundle without it.
  goto :fail
)
if not exist "%SDATA%\SKSE\Plugins\MpClientPlugin.dll" (
  echo [STOP] Data\SKSE\Plugins\MpClientPlugin.dll is missing.
  echo        The bundle must carry it - it is the network half of the client.
  goto :fail
)
if not exist "%SDATA%\SKSE\Plugins\SkyrimPlatform.dll" (
  echo [STOP] Data\SKSE\Plugins\SkyrimPlatform.dll is missing.
  echo        The bundle must carry it - it runs the whole client bundle.
  goto :fail
)
if not exist "%SDATA%\Platform\Distribution\" (
  echo [STOP] Data\Platform\Distribution is missing - that folder holds the
  echo        CEF browser runtime, the bulk of the bundle.
  goto :fail
)

REM --- clean any previous staging folder --------------------------------------
if exist "%STAGE%\" (
  echo Cleaning old staging folder...
  rmdir /S /Q "%STAGE%"
  if exist "%STAGE%\" (
    echo [STOP] Could not delete the old staging folder:
    echo        %STAGE%
    echo        Close any Explorer window sitting inside it and run again.
    goto :fail
  )
)

REM --- stage: whole shipped folders -------------------------------------------
echo Copying Platform\Distribution ...
REM  /XF ___systemPolyfill.js: SkyrimPlatform writes that scratch file into its
REM  own folder while the game runs. It is machine litter, not bundle. It is why
REM  a build once came out at 111 entries instead of 109.
robocopy "%SDATA%\Platform\Distribution" "%STAGE%\Data\Platform\Distribution" /E /XF ___systemPolyfill.js /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 ( echo [STOP] Copying Distribution failed. & goto :fail )

echo Copying Platform\Fonts ...
robocopy "%SDATA%\Platform\Fonts" "%STAGE%\Data\Platform\Fonts" /E /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 ( echo [STOP] Copying Fonts failed. & goto :fail )

echo Copying Platform\Modules ...
robocopy "%SDATA%\Platform\Modules" "%STAGE%\Data\Platform\Modules" /E /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 ( echo [STOP] Copying Modules failed. & goto :fail )

echo Copying Platform\plugin-example ...
REM  /XF .gitignore: hidden repo housekeeping file the golden bundle omits.
robocopy "%SDATA%\Platform\plugin-example" "%STAGE%\Data\Platform\plugin-example" /E /XF .gitignore /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 ( echo [STOP] Copying plugin-example failed. & goto :fail )

REM --- stage: Plugins - ONLY the client js, NEVER your live settings ----------
REM  Copying this folder wholesale would ship your live
REM  skymp5-client-settings.txt (your profileId and server IP) to every player,
REM  and would also sweep in every .bak left behind by hand-patching. One
REM  explicit copy avoids both.
mkdir "%STAGE%\Data\Platform\Plugins" 2>nul
copy /Y "%SDATA%\Platform\Plugins\skymp5-client.js" "%STAGE%\Data\Platform\Plugins\" >nul
REM --- the Holdline plugin ships to every player - 21 Sep 2026 -----------
REM  Every player window (shop, storage, travel, gifts) lives in this file.
REM  It was only ever on Ben's PC, so the first outside tester got merchants
REM  with a voice line and no shop. The plugin gates its admin menu to the
REM  admin profile itself; the server refuses admin commands from anyone else.
if not exist "%SDATA%\Platform\Plugins\z-holdline-admin.js" (
  echo [STOP] Missing Data\Platform\Plugins\z-holdline-admin.js - the bundle ships it.
  goto :fail
)
copy /Y "%SDATA%\Platform\Plugins\z-holdline-admin.js" "%STAGE%\Data\Platform\Plugins\" >nul
if not exist "%STAGE%\Data\Platform\Plugins\skymp5-client.js" (
  echo [STOP] Could not stage skymp5-client.js.
  goto :fail
)

REM --- stage: Platform\UI and Platform\Images - ADDED 19 Sep 2026 ------------
REM  UI\index.html is the page the browser opens at startup. Ben's and Besnik's
REM  machines have it hand-placed, so the bundle never carried it and nobody
REM  noticed: a fresh machine without it gets the white ERR_FILE_NOT_FOUND
REM  page that bricked Fix My Game on 9 Aug. Images rides along the same way.
echo Copying Platform\UI ...
if not exist "%SDATA%\Platform\UI\index.html" (
  echo [STOP] Missing Data\Platform\UI\index.html - the bundle ships it.
  goto :fail
)
robocopy "%SDATA%\Platform\UI" "%STAGE%\Data\Platform\UI" /E /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 ( echo [STOP] Copying UI failed. & goto :fail )

echo Copying Platform\Images ...
if not exist "%SDATA%\Platform\Images\" (
  echo [STOP] Missing Data\Platform\Images - the bundle ships it.
  goto :fail
)
robocopy "%SDATA%\Platform\Images" "%STAGE%\Data\Platform\Images" /E /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 ( echo [STOP] Copying Images failed. & goto :fail )

REM --- stage: the required EMPTY PluginsDev folder ----------------------------
REM  SkyrimPlatform silently kills its JS engine when this folder is absent.
mkdir "%STAGE%\Data\Platform\PluginsDev" 2>nul
if not exist "%STAGE%\Data\Platform\PluginsDev\" ( echo [STOP] Could not create PluginsDev. & goto :fail )

REM --- stage: SKSE plugins - the two DLLs + the eight versionlib files --------
mkdir "%STAGE%\Data\SKSE\Plugins" 2>nul
copy /Y "%SDATA%\SKSE\Plugins\MpClientPlugin.dll"  "%STAGE%\Data\SKSE\Plugins\" >nul
copy /Y "%SDATA%\SKSE\Plugins\SkyrimPlatform.dll"  "%STAGE%\Data\SKSE\Plugins\" >nul
for %%V in (
  versionlib-1-6-1170-0-1.bin
  versionlib-1-6-1170-0.bin
  versionlib-1-6-317-0.bin
  versionlib-1-6-318-0.bin
  versionlib-1-6-323-0.bin
  versionlib-1-6-342-0.bin
  versionlib-1-6-353-0.bin
  versionlib-1-6-640-0.bin
) do (
  if not exist "%SDATA%\SKSE\Plugins\%%V" (
    echo [STOP] Missing %%V in Data\SKSE\Plugins - the bundle ships it.
    goto :fail
  )
  copy /Y "%SDATA%\SKSE\Plugins\%%V" "%STAGE%\Data\SKSE\Plugins\" >nul
)

REM --- Engine Fixes: NOT SHIPPED. Retracted 19 Sep 2026. -----------------
REM  The 27 Aug note that stood here said EngineFixes.dll loads quietly as an
REM  ordinary SKSE plugin when EngineFixes_preload.txt is left out. It does
REM  not. The first true fresh-machine Join (19 Sep) got this popup from the
REM  DLL and Skyrim closed: "Engine Fixes did not pre-load and fixes are not
REM  active ... d3dx9_42.dll ... Skyrim will now close." The DLL demands the
REM  preloader whether or not the marker file exists. The preloader is
REM  banned (it killed Gio's game on 27 Aug), so the DLL cannot ship either.
REM  Ben's and Besnik's machines only get away with it because both have the
REM  preloader hand-placed in the Skyrim root.
REM  Nothing here is staged. Anything Engine Fixes that reached staging by
REM  another route is removed, and the zip is refused if the DLL survives.
for %%E in (
  EngineFixes.dll
  EngineFixes.pdb
  EngineFixes.toml
  EngineFixes_SNCT.ini
  EngineFixes_preload.txt
) do (
  if exist "%STAGE%\Data\SKSE\Plugins\%%E" del /F /Q "%STAGE%\Data\SKSE\Plugins\%%E"
)
if exist "%STAGE%\Data\SKSE\Plugins\EngineFixes.dll" (
  echo [STOP] EngineFixes.dll is still in staging. It must not ship.
  goto :fail
)
echo   Engine Fixes     : not shipped (correct - see note in this file)

REM --- stage: Interface overlay ----------------------------------------------
mkdir "%STAGE%\Data\Interface" 2>nul
if not exist "%SDATA%\Interface\CombatAlertOverlayMenu.swf" (
  echo [STOP] Missing Data\Interface\CombatAlertOverlayMenu.swf - the bundle ships it.
  goto :fail
)
copy /Y "%SDATA%\Interface\CombatAlertOverlayMenu.swf" "%STAGE%\Data\Interface\" >nul

REM --- stage: the script files + sources -------------------------------------
mkdir "%STAGE%\Data\Scripts\Source" 2>nul
for %%S in (
  DLC2SummonDremoraMerchantScript.pex
  MpClientPlugin.pex
  QF_MQ201_00035D5F.pex
  TESModPlatform.pex
  TIF__000361DF.pex
  playerbookshelfcontainerscript.pex
  uimenubase.pex
) do (
  if not exist "%SDATA%\Scripts\%%S" (
    echo [STOP] Missing Data\Scripts\%%S - the bundle ships it.
    goto :fail
  )
  copy /Y "%SDATA%\Scripts\%%S" "%STAGE%\Data\Scripts\" >nul
)
for %%S in (
  DLC2SummonDremoraMerchantScript.psc
  PlayerBookShelfContainerScript.psc
  QF_MQ201_00035D5F.psc
  TIF__000361DF.psc
  uimenubase.psc
) do (
  if not exist "%SDATA%\Scripts\Source\%%S" (
    echo [STOP] Missing Data\Scripts\Source\%%S - the bundle ships it.
    goto :fail
  )
  copy /Y "%SDATA%\Scripts\Source\%%S" "%STAGE%\Data\Scripts\Source\" >nul
)

REM --- stage: Root - present for the launcher, carrying NO engine files -------
REM  The launcher aborts the whole install if this folder is absent from the
REM  zip, so it ships. What it must NOT ship is d3dx9_42.dll. A README keeps the
REM  folder real, keeps the entry count at 109, and tells anyone who finds it in
REM  their Skyrim folder what it is.
mkdir "%STAGE%\Root" 2>nul
if not exist "%STAGE%\Root\" ( echo [STOP] Could not create the Root folder. & goto :fail )
set "ROOTNOTE=%STAGE%\Root\HOLDLINE-README.txt"
> "%ROOTNOTE%" echo Holdline - files that belong beside SkyrimSE.exe
>> "%ROOTNOTE%" echo.
>> "%ROOTNOTE%" echo This folder is intentionally almost empty.
>> "%ROOTNOTE%" echo.
>> "%ROOTNOTE%" echo It used to carry d3dx9_42.dll, the SKSE preloader for SSE
>> "%ROOTNOTE%" echo Engine Fixes. That file was removed on 17 Aug 2026: on a PC
>> "%ROOTNOTE%" echo without the real DirectX redistributable already installed it
>> "%ROOTNOTE%" echo replaces real DirectX with a stub, and Skyrim dies about four
>> "%ROOTNOTE%" echo seconds into startup with no error message of any kind.
>> "%ROOTNOTE%" echo.
>> "%ROOTNOTE%" echo Holdline does not need it. Nothing here is required for the
>> "%ROOTNOTE%" echo game to run - the multiplayer files all live under Data\.
>> "%ROOTNOTE%" echo.
>> "%ROOTNOTE%" echo You can delete this text file. Do not add DLLs to it.
if not exist "%ROOTNOTE%" ( echo [STOP] Could not write the Root README. & goto :fail )

REM --- stage: a CLEAN settings template, generated fresh ----------------------
REM  Copying the live file would ship YOUR profileId and server IP to every
REM  player. The launcher rewrites this file with the player's own details on
REM  every Join, so the template only needs to exist and be valid JSON.
set "SETTINGS=%STAGE%\Data\Platform\Plugins\skymp5-client-settings.txt"
> "%SETTINGS%" echo {
>> "%SETTINGS%" echo   "gameData" :
>> "%SETTINGS%" echo   {
>> "%SETTINGS%" echo     "profileId" : 1
>> "%SETTINGS%" echo   },
>> "%SETTINGS%" echo   "master" : "",
>> "%SETTINGS%" echo   "server-ip" : "127.0.0.1",
>> "%SETTINGS%" echo   "server-master-key" : null,
>> "%SETTINGS%" echo   "server-port" : 7777
>> "%SETTINGS%" echo }
if not exist "%SETTINGS%" ( echo [STOP] Could not write the settings template. & goto :fail )

REM --- refuse the banned preloader, wherever it came from ---------------------
REM  Belt and braces: if a copy is ever sitting inside one of the folders copied
REM  above, /E would sweep it in.
if exist "%STAGE%\Root\d3dx9_42.dll" (
  echo [STOP] d3dx9_42.dll got staged into Root. It is banned from the bundle.
  goto :fail
)
if exist "%STAGE%\Data\Platform\d3dx9_42.dll" (
  echo [STOP] d3dx9_42.dll is sitting in Data\Platform and got staged.
  echo        It is banned from the bundle. Delete it and run again.
  goto :fail
)

REM --- report the build stamp actually being shipped --------------------------
REM  The one line that says whether this zip has the build you just tested.
echo.
echo Build stamp in the staged bundle:
REM  No for /f and no pipe here on purpose. Escaping rules differ between a
REM  quoted string and a for /f backquote, and getting it wrong is exactly what
REM  produced the parser error this rebuild is fixing. PowerShell prints it.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$m=@(Select-String -Path '%STAGE%\Data\Platform\Plugins\skymp5-client.js' -Pattern 'BUNDLE BUILD ([a-zA-Z0-9._-]+)');" ^
  "if($m.Count -gt 0){ Write-Host ('  ' + $m[0].Matches[0].Groups[1].Value) } else { Write-Host '  (no BUNDLE BUILD stamp found - is this the right bundle?)' }"

REM --- zip - CreateFromDirectory keeps the empty PluginsDev folder ------------
REM  Compress-Archive silently DROPS empty folders, which is how an older
REM  script could never ship PluginsDev. The .NET zip API keeps it.
echo.
echo Zipping...
if exist "%ZIP%" del /F /Q "%ZIP%"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "Add-Type -AssemblyName System.IO.Compression.FileSystem;" ^
  "[IO.Compression.ZipFile]::CreateFromDirectory('%STAGE%','%ZIP%')"
if not exist "%ZIP%" ( echo [STOP] The zip was not produced. & goto :fail )

REM --- read the finished zip back and prove the shape -------------------------
echo Verifying the finished zip...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "Add-Type -AssemblyName System.IO.Compression.FileSystem;" ^
  "$z=[IO.Compression.ZipFile]::OpenRead('%ZIP%');" ^
  "$n=@($z.Entries | ForEach-Object { $_.FullName.Replace('\','/') });" ^
  "$count=$n.Count; $z.Dispose();" ^
  "$need=@('Data/Platform/Plugins/skymp5-client.js','Data/Platform/Plugins/skymp5-client-settings.txt','Data/Platform/PluginsDev/','Data/Platform/UI/index.html','Data/Platform/Plugins/z-holdline-admin.js','Data/SKSE/Plugins/MpClientPlugin.dll','Data/SKSE/Plugins/SkyrimPlatform.dll','Root/HOLDLINE-README.txt');" ^
  "$bad=0; foreach($x in $need){ if($n -notcontains $x){ Write-Host ('[STOP] zip is missing ' + $x); $bad=1 } };" ^
  "$banned=@($n | Where-Object { $_ -match 'd3dx9_42' });" ^
  "if($banned.Count -gt 0){ Write-Host '[STOP] zip contains the banned d3dx9_42.dll'; $bad=1 };" ^
  "if(($n | Where-Object { $_ -match 'EngineFixes' }).Count -gt 0){ Write-Host '[STOP] zip contains Engine Fixes - it must not ship (19 Sep)'; $bad=1 };" ^
  "Write-Host ('  total entries : ' + $count + '   - 210 on 19 Sep included 3 Engine Fixes files that no longer ship, so 207 was the count; 208 with the Holdline plugin added 21 Sep');" ^
  "if($count -lt 110){" ^
  "  $n | Set-Content -Path '%OUTDIR%\zip-entry-list.txt';" ^
  "  Write-Host '  COUNT DIFFERS from the golden bundle.';" ^
  "  Write-Host '  Full listing written to zip-entry-list.txt next to the zip.';" ^
  "  Write-Host '  Fewer entries than before means a folder did not stage.';" ^
  "  Write-Host '  own folders while you played. Send that list before releasing.'" ^
  "};" ^
  "if($bad){ exit 1 };" ^
  "Write-Host '  shape check   : all required entries present';" ^
  "Write-Host '  d3dx9_42.dll  : absent (correct)';" ^
  "Write-Host '  EngineFixes   : absent (correct)'"
if errorlevel 1 (
  echo.
  echo The zip failed verification. Do NOT upload it.
  goto :fail
)

REM --- fingerprints ----------------------------------------------------------
REM  One tiny powershell call per hash - no braces, no if/else inside PS, and
REM  no exclamation marks anywhere. Batch does the comparing.
REM
REM  WHY THIS FILE CONTAINS NO EXCLAMATION MARK: the script runs under
REM  EnableDelayedExpansion, where cmd treats that character as a variable
REM  marker and silently DELETES text around it. The earlier version of this
REM  block ended a string with the words "update ClientFiles.cs" followed by
REM  one, and cmd chewed the rest of the line, which reached PowerShell as unbalanced
REM  braces: "Missing closing '}' in statement block". The braces were never the
REM  problem. Do not put an exclamation mark in this file.
REM
REM  THREE hashes, answering three different questions:
REM    EXP_JS    what ClientFiles.cs ClientBundleSha256 currently expects.
REM              Differs whenever you patch the bundle - that is normal, but it
REM              means the launcher constant must be updated.
REM    TESTED_JS the bundle build that was confirmed working in game on 17 Aug.
REM              If the staged file does not match this, you are about to ship
REM              something other than what was tested.
REM    EXP_MP / EXP_SP  the two SKSE DLLs. Verified against the real bytes on
REM              17 Aug; they have not changed since 6 Aug. A mismatch here
REM              means a DLL was swapped, which nothing in Holdline should do.
set "EXP_JS=004e702605510742254716f539aee33b440ed4d89592d36aec02d042c3f87f36"
set "TESTED_JS=F340B034C206CCDCE7D26444C701380D253E3497F4179B58AF262D560A02D376"
set "EXP_MP=391CCD3AB48B527EB35C95CC104C50A46412BD2AF9946E9427FCE7BA110177A9"
set "EXP_SP=64BE0221A95ECDA5DDC9DFA98CBFC9AF35CFC7CCA35AF96230A96568ED2C24CD"

for /f %%H in ('powershell -NoProfile -Command "(Get-FileHash -Algorithm SHA256 '%STAGE%\Data\Platform\Plugins\skymp5-client.js').Hash"') do set "H_JS=%%H"
for /f %%H in ('powershell -NoProfile -Command "(Get-FileHash -Algorithm SHA256 '%STAGE%\Data\SKSE\Plugins\MpClientPlugin.dll').Hash"') do set "H_MP=%%H"
for /f %%H in ('powershell -NoProfile -Command "(Get-FileHash -Algorithm SHA256 '%STAGE%\Data\SKSE\Plugins\SkyrimPlatform.dll').Hash"') do set "H_SP=%%H"
for /f %%H in ('powershell -NoProfile -Command "(Get-FileHash -Algorithm SHA256 '%ZIP%').Hash"') do set "H_ZIP=%%H"

echo.
echo Fingerprints
set "DIFF=0"
set "WRONGBUILD=0"

echo   skymp5-client.js   : %H_JS%
if /i "%H_JS%"=="%TESTED_JS%" (
  echo        is the 17 Aug build confirmed working in game
) else (
  echo        NOT the 17 Aug tested build - check what is in Data\Platform\Plugins
  set "WRONGBUILD=1"
)
if /i "%H_JS%"=="%EXP_JS%" (
  echo        matches ClientBundleSha256 - no launcher edit needed
) else (
  echo        differs from ClientBundleSha256 - launcher edit needed, see below
  set "DIFF=1"
)

echo   MpClientPlugin.dll : %H_MP%
if /i "%H_MP%"=="%EXP_MP%" (
  echo        unchanged since the verified bundle
) else (
  echo        CHANGED - nothing in Holdline should alter this DLL. Stop and ask.
)
echo   SkyrimPlatform.dll : %H_SP%
if /i "%H_SP%"=="%EXP_SP%" (
  echo        unchanged since the verified bundle
) else (
  echo        CHANGED - nothing in Holdline should alter this DLL. Stop and ask.
)
echo.
echo   Data.zip           : %H_ZIP%

if "%WRONGBUILD%"=="1" (
  echo.
  echo ----------------------------------------------------------
  echo  WARNING: the bundle being packed is not the one tested on 17 Aug.
  echo  The zip was still built, but do not release it until you know why.
  echo ----------------------------------------------------------
)

if "%DIFF%"=="1" (
  echo.
  echo ----------------------------------------------------------
  echo  THE CLIENT BUNDLE CHANGED. That is expected if you patched it.
  echo.
  echo  Update ALL THREE in ClientFiles.cs in ONE edit, then publish the
  echo  release BEFORE shipping a launcher that points at it. Miss one and
  echo  every launcher re-downloads 180MB on every single join, forever.
  echo.
  echo    1. ClientFilesUrl          - point at the new release tag
  echo    2. ClientFilesVersion      - bump it, any new string
  echo    3. ClientBundleSha256      - paste this, line ~137:
  echo.
  echo       %H_JS%
  echo.
  echo ----------------------------------------------------------
)

REM --- tidy ------------------------------------------------------------------
rmdir /S /Q "%STAGE%" 2>nul

echo.
echo ==========================================================
echo  DONE. Upload this file as the release asset, named exactly Data.zip:
echo  %ZIP%
echo ==========================================================
echo.
pause
exit /b 0

:fail
echo.
echo Build stopped. Nothing was uploaded, nothing was broken.
echo.
pause
exit /b 1
