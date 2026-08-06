@echo off
setlocal EnableExtensions
cd /d "%~dp0\.."

echo ============================================================
echo CP MIGRATION - BUILDER V3 ESPECIALIZADO
echo ============================================================
echo.

set "SOURCE=%CD%\output\erp_intermediario.sqlite"
set "OUTPUT=%CD%\NovoERP-V3"

if not "%~1"=="" set "SOURCE=%~1"
if not "%~2"=="" set "OUTPUT=%~2"

echo Origem: %SOURCE%
echo Saida:  %OUTPUT%
echo.

if not exist "%SOURCE%" (
  echo ERRO: erp_intermediario.sqlite nao encontrado.
  echo %SOURCE%
  pause
  exit /b 1
)

echo Restaurando dependencias...
dotnet restore src\CPMigration.BuilderV3\CPMigration.BuilderV3.csproj --disable-parallel
if errorlevel 1 goto :erro

echo Compilando...
dotnet build src\CPMigration.BuilderV3\CPMigration.BuilderV3.csproj -c Release --no-restore
if errorlevel 1 goto :erro

echo Executando Builder V3...
dotnet run --project src\CPMigration.BuilderV3\CPMigration.BuilderV3.csproj -c Release --no-build -- --source "%SOURCE%" --output "%OUTPUT%"
if errorlevel 1 goto :erro

echo.
echo ============================================================
echo BUILDER V3 CONCLUIDO
echo Resultado: %OUTPUT%
echo ============================================================
start "" "%OUTPUT%"
pause
exit /b 0

:erro
set "ERR=%ERRORLEVEL%"
echo.
echo ============================================================
echo FALHA NO BUILDER V3
echo Codigo: %ERR%
echo Consulte: %OUTPUT%\BUILDER_V3.log
echo ============================================================
pause
exit /b %ERR%
