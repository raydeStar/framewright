@echo off
rem A controlled stand-in for the Reference Asset Compiler in browser journeys.
rem The studio starts an executable, so this forwards to the PowerShell that
rem does the work. See e2e-compiler-stub.ps1 for why it exists.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0e2e-compiler-stub.ps1" %*
