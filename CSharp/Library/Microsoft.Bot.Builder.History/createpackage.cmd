﻿@echo off
setlocal
setlocal enabledelayedexpansion
setlocal enableextensions
set errorlevel=0
erase /s *.nupkg
msbuild /property:Configuration=release Microsoft.Bot.Builder.History.csproj 
for /f %%v in ('powershell -noprofile "(Get-Command .\bin\release\Microsoft.Bot.Builder.History.dll).FileVersionInfo.FileVersion"') do set version=%%v
..\..\packages\NuGet.CommandLine.3.4.3\tools\NuGet.exe pack Microsoft.Bot.Builder.History.nuspec -symbols -properties version=%version%

