@echo off
@setlocal
set ERROR_CODE=0

dotnet ".\AzSync.CLI\bin\Debug\netcoreapp2.0\AzSync.CLI.dll" %*
goto end

:end
exit /B %ERROR_CODE%