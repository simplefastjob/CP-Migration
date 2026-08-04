@echo off
setlocal
cd /d "%~dp0"
chcp 65001 >nul
set NUGET_HTTP_TIMEOUT_SECONDS=300

where dotnet >nul 2>&1
if errorlevel 1 (
  echo ERRO: .NET 8 SDK nao encontrado.
  pause
  exit /b 1
)

if not exist input mkdir input
if not exist output mkdir output

for /l %%I in (1,1,4) do (
  echo Restaurando dependencias - tentativa %%I de 4...
  dotnet restore src\CPMigration.Cli\CPMigration.Cli.csproj --disable-parallel
  if not errorlevel 1 goto :restored
  timeout /t 15 /nobreak >nul
)

echo FALHA AO RESTAURAR DEPENDENCIAS.
pause
exit /b 1

:restored
echo Compilando...
dotnet build src\CPMigration.Cli\CPMigration.Cli.csproj -c Release --no-restore
if errorlevel 1 (
  echo FALHA DE COMPILACAO.
  pause
  exit /b 1
)

echo Executando pipeline completo...
dotnet run --project src\CPMigration.Cli\CPMigration.Cli.csproj -c Release --no-build -- --input "%CD%\input" --output "%CD%\output"
if errorlevel 1 (
  echo PROCESSAMENTO INTERROMPIDO. Consulte output\pipeline.log
  pause
  exit /b 1
)

start "" "%CD%\output\Migration"
echo CONCLUIDO.
pause
