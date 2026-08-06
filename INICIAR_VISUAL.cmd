@echo off
setlocal
cd /d "%~dp0"
chcp 65001 >nul

where dotnet >nul 2>&1
if errorlevel 1 (
  echo ERRO: .NET 8 SDK nao encontrado.
  echo Instale o .NET 8 SDK e tente novamente.
  pause
  exit /b 1
)

echo Restaurando e compilando a interface...
dotnet restore src\CPMigration.Desktop\CPMigration.Desktop.csproj --disable-parallel
if errorlevel 1 goto :error

dotnet build src\CPMigration.Desktop\CPMigration.Desktop.csproj -c Release --no-restore
if errorlevel 1 goto :error

start "CP Migration Studio" "%CD%\src\CPMigration.Desktop\bin\Release\net8.0-windows\CPMigration.exe"
exit /b 0

:error
echo.
echo FALHA AO ABRIR A INTERFACE.
echo Consulte os erros acima.
pause
exit /b 1
