@echo off
setlocal EnableExtensions
cd /d "%~dp0\.."

echo ============================================================
echo CP MIGRATION - BUILDER V4 TIPADO
echo ============================================================
echo.

set "SOURCE=%CD%\output\erp_intermediario.sqlite"
set "OUTPUT=%CD%\NovoERP-V4"
if not "%~1"=="" set "SOURCE=%~1"
if not "%~2"=="" set "OUTPUT=%~2"

echo Origem: %SOURCE%
echo Saida:  %OUTPUT%
echo.
if not exist "%SOURCE%" (
  echo ERRO: banco intermediario nao encontrado.
  pause
  exit /b 1
)

dotnet restore src\CPMigration.BuilderV4\CPMigration.BuilderV4.csproj --disable-parallel
if errorlevel 1 goto :erro
dotnet build src\CPMigration.BuilderV4\CPMigration.BuilderV4.csproj -c Release --no-restore
if errorlevel 1 goto :erro
dotnet run --project src\CPMigration.BuilderV4\CPMigration.BuilderV4.csproj -c Release --no-build -- --source "%SOURCE%" --output "%OUTPUT%"
if errorlevel 1 goto :erro

echo.
echo ============================================================
echo BUILDER V4 CONCLUIDO
echo Resultado: %OUTPUT%
echo ============================================================
start "" "%OUTPUT%"
pause
exit /b 0

:erro
set "ERR=%ERRORLEVEL%"
echo.
echo ============================================================
echo FALHA NO BUILDER V4 - CODIGO %ERR%
echo Consulte %OUTPUT%\BUILDER_V4.log
echo ============================================================
pause
exit /b %ERR%
