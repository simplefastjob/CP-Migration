@echo off
setlocal EnableExtensions
cd /d "%~dp0\.."

echo ============================================================
echo CP MIGRATION - DEEP ANALYZER PARTE 1
echo ============================================================
echo.

set "DATABASE=%CD%\output\erp_intermediario.sqlite"
set "OUTPUT=%CD%\AnaliseCompleta"

if not "%~1"=="" set "DATABASE=%~1"
if not "%~2"=="" set "OUTPUT=%~2"

echo Banco: %DATABASE%
echo Saida: %OUTPUT%
echo.

if not exist "%DATABASE%" (
  echo ERRO: Banco nao encontrado.
  echo %DATABASE%
  pause
  exit /b 1
)

echo Restaurando dependencias...
dotnet restore src\CPMigration.DeepAnalyzer\CPMigration.DeepAnalyzer.csproj --disable-parallel
if errorlevel 1 goto :erro

echo Compilando...
dotnet build src\CPMigration.DeepAnalyzer\CPMigration.DeepAnalyzer.csproj -c Release --no-restore
if errorlevel 1 goto :erro

echo Executando analise profunda...
dotnet run --project src\CPMigration.DeepAnalyzer\CPMigration.DeepAnalyzer.csproj -c Release --no-build -- --database "%DATABASE%" --output "%OUTPUT%"
if errorlevel 1 goto :erro

echo.
echo ============================================================
echo ANALISE CONCLUIDA
echo Resultado: %OUTPUT%
echo ============================================================
start "" "%OUTPUT%\index.html"
pause
exit /b 0

:erro
set "ERR=%ERRORLEVEL%"
echo.
echo ============================================================
echo FALHA NA ANALISE
echo Codigo: %ERR%
echo Consulte: %OUTPUT%\LOG_COMPLETO.txt
echo ============================================================
pause
exit /b %ERR%
