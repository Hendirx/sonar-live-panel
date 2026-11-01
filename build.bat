@echo off
rem Builds SonarLivePanel.exe and the installer dist\SonarLivePanel-Setup.exe with the
rem C# compiler that ships with Windows (.NET Framework 4). Close the panel first.
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe

"%CSC%" /nologo /target:winexe /optimize+ /win32icon:app.ico /out:SonarLivePanel.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll LivePanel.cs
if errorlevel 1 goto fail
echo Built SonarLivePanel.exe

if not exist dist mkdir dist
"%CSC%" /nologo /target:winexe /optimize+ /win32icon:app.ico /win32manifest:installer\setup.manifest /out:dist\SonarLivePanel-Setup.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:Microsoft.CSharp.dll ^
  /resource:SonarLivePanel.exe,SonarLivePanel.exe /resource:README.md,README.md /resource:settings.ini,settings.ini ^
  installer\Setup.cs
if errorlevel 1 goto fail
echo Built dist\SonarLivePanel-Setup.exe
exit /b 0

:fail
echo Build failed.
pause
exit /b 1
