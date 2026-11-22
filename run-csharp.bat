@echo off
title Claude Usage Tracker (C# .NET)
REM Truoc day tro vao "%~dp0bin" - thu muc nam trong .gitignore nen ban clone moi
REM khong he co, script chay la loi ngay. Build roi chay tu dung thu muc output.
cd /d "%~dp0csharp"

set "EXE=bin\Release\net11.0-windows\ClaudeUsageTracker.exe"
if not exist "%EXE%" (
    echo Chua co ban build, dang build...
    dotnet build ClaudeUsageTracker.csproj -c Release || (
        echo Build that bai.
        pause
        exit /b 1
    )
)
start "" "%EXE%"
exit /b 0
