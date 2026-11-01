@echo off
rem Builds and runs the protocol tests (tests\McuTest.cs) against LivePanel.cs. No Sonar needed.
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist dist mkdir dist
"%CSC%" /nologo /target:exe /main:LivePanel.McuTest /out:dist\McuTest.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll LivePanel.cs tests\McuTest.cs
if errorlevel 1 exit /b 1
dist\McuTest.exe
