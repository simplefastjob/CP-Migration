@echo off
setlocal EnableExtensions
cd /d "%~dp0\.."

echo ============================================================
echo CP MIGRATION - DEEP ANALYZER COMPLETO
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

echo [1/2] Restaurando e compilando analise estrutural...
dotnet restore src\CPMigration.DeepAnalyzer\CPMigration.DeepAnalyzer.csproj --disable-parallel
if errorlevel 1 goto :erro
dotnet build src\CPMigration.DeepAnalyzer\CPMigration.DeepAnalyzer.csproj -c Release --no-restore
if errorlevel 1 goto :erro

echo [1/2] Executando analise estrutural e perfil dos dados...
dotnet run --project src\CPMigration.DeepAnalyzer\CPMigration.DeepAnalyzer.csproj -c Release --no-build -- --database "%DATABASE%" --output "%OUTPUT%"
if errorlevel 1 goto :erro

echo.
echo [2/2] Restaurando e compilando descoberta de relacionamentos...
dotnet restore src\CPMigration.DeepAnalyzer.Relationships\CPMigration.DeepAnalyzer.Relationships.csproj --disable-parallel
if errorlevel 1 goto :erro
dotnet build src\CPMigration.DeepAnalyzer.Relationships\CPMigration.DeepAnalyzer.Relationships.csproj -c Release --no-restore
if errorlevel 1 goto :erro

echo [2/2] Cruzando chaves, valores e dominios...
dotnet run --project src\CPMigration.DeepAnalyzer.Relationships\CPMigration.DeepAnalyzer.Relationships.csproj -c Release --no-build -- --database "%DATABASE%" --structure "%OUTPUT%\EstruturaCompleta.json" --output "%OUTPUT%"
if errorlevel 1 goto :erro

echo.
echo ============================================================
echo ANALISE COMPLETA CONCLUIDA
echo Resultado: %OUTPUT%
echo ============================================================
start "" "%OUTPUT%"
pause
exit /b 0

:erro
set "ERR=%ERRORLEVEL%"
echo.
echo ============================================================
echo FALHA NA ANALISE
echo Codigo: %ERR%
echo Consulte os logs dentro de: %OUTPUT%
echo ============================================================
pause
exit /b %ERR%
