@echo off
rem ---------------------------------------------------------------------------
rem  WavelineBase automated checks (Unity Editor must be CLOSED for this project)
rem  Compiles all scripts and runs every EditMode test in batch mode, then writes
rem    Logs\TestResults\summary.txt        short pass/fail report (+ compile errors)
rem    Logs\TestResults\editmode-results.xml   full NUnit results
rem    Logs\TestResults\editmode-editor.log    the batch Editor log
rem  Exit code: 0 = all passed, 1 = failures or compile errors, 2 = Unity not found.
rem  Unity path: set UNITY_EXE to override; otherwise the Unity Hub default install
rem  for the version in ProjectSettings\ProjectVersion.txt is used.
rem ---------------------------------------------------------------------------
setlocal
set "PROJECT=%~dp0.."
for %%I in ("%PROJECT%") do set "PROJECT=%%~fI"

for /f "tokens=2" %%v in ('findstr /b /c:"m_EditorVersion:" "%PROJECT%\ProjectSettings\ProjectVersion.txt"') do set "UNITY_VERSION=%%v"
if not defined UNITY_EXE set "UNITY_EXE=%ProgramFiles%\Unity\Hub\Editor\%UNITY_VERSION%\Editor\Unity.exe"
if not exist "%UNITY_EXE%" (
    echo Unity %UNITY_VERSION% not found at "%UNITY_EXE%".
    echo Set UNITY_EXE to your Unity.exe path, e.g.  set UNITY_EXE=D:\Unity\%UNITY_VERSION%\Editor\Unity.exe
    exit /b 2
)

set "OUT=%PROJECT%\Logs\TestResults"
if not exist "%OUT%" mkdir "%OUT%"
del /q "%OUT%\editmode-results.xml" 2>nul

echo Running WavelineBase checks with Unity %UNITY_VERSION% ...
echo (Close the Unity Editor first - batch mode can't open a project that's already open.)
"%UNITY_EXE%" -batchmode -nographics -projectPath "%PROJECT%" -runTests -testPlatform EditMode ^
    -testResults "%OUT%\editmode-results.xml" -logFile "%OUT%\editmode-editor.log"
set "UNITY_CODE=%ERRORLEVEL%"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0summarize-checks.ps1" ^
    -ResultsXml "%OUT%\editmode-results.xml" -EditorLog "%OUT%\editmode-editor.log" ^
    -SummaryPath "%OUT%\summary.txt" -UnityExitCode %UNITY_CODE%
exit /b %ERRORLEVEL%
