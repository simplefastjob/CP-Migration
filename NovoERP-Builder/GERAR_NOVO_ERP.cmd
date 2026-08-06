@echo off
setlocal
cd /d "%~dp0\.."

echo ============================================================
echo CP MIGRATION - BUILDER DO NOVO ERP
echo ============================================================
echo.

set "SOURCE=%CD%\output\Migration\CONSOLIDADO"
set "DEST=%CD%\NovoERP"

if not exist "%SOURCE%" (
  echo ERRO: Pasta consolidada nao encontrada:
  echo %SOURCE%
  echo.
  pause
  exit /b 1
)

if not exist "%DEST%" mkdir "%DEST%"

echo Restaurando dependencias...
dotnet restore src\CPMigration.Builder\CPMigration.Builder.csproj --disable-parallel
if errorlevel 1 goto :erro

echo Compilando...
dotnet build src\CPMigration.Builder\CPMigration.Builder.csproj -c Release --no-restore
if errorlevel 1 goto :erro

echo Convertendo arquivos compactados para NovoERP.sqlite...
dotnet run --project src\CPMigration.Builder\CPMigration.Builder.csproj -c Release --no-build -- --source "%SOURCE%" --output "%DEST%"
if errorlevel 1 goto :erro

echo.
echo ============================================================
echo CONCLUIDO
 echo Resultado: %DEST%
echo ============================================================
start "" "%DEST%"
pause
exit /b 0

:erro
echo.
echo ============================================================
echo FALHA AO GERAR O NOVO ERP
 echo Confira as mensagens acima.
echo ============================================================
pause
exit /b 1
