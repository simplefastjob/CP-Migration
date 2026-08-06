@echo off
setlocal EnableExtensions
cd /d "%~dp0\.."

echo ============================================================
echo CP MIGRATION - BUILDER DO NOVO ERP
echo ============================================================
echo.

set "SOURCE=%CD%\output\Migration\CONSOLIDADO"
set "DEST=%CD%\NovoERP"

if not "%~1"=="" set "DEST=%~1"

if not exist "%SOURCE%" (
  echo ERRO: Pasta consolidada nao encontrada:
  echo %SOURCE%
  echo.
  pause
  exit /b 1
)

if not exist "%DEST%" mkdir "%DEST%"

echo Origem: %SOURCE%
echo Saida:  %DEST%
echo.

echo Restaurando dependencias...
dotnet restore src\CPMigration.Builder\CPMigration.Builder.csproj --disable-parallel
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" goto :erro

echo Compilando...
dotnet build src\CPMigration.Builder\CPMigration.Builder.csproj -c Release --no-restore
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" goto :erro

echo Convertendo arquivos compactados para NovoERP.sqlite...
dotnet run --project src\CPMigration.Builder\CPMigration.Builder.csproj -c Release --no-build -- --source "%SOURCE%" --output "%DEST%"
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" goto :erro

if not exist "%DEST%\NovoERP.sqlite" (
  set "RC=2"
  echo ERRO: O processo terminou sem criar NovoERP.sqlite.
  goto :erro
)

echo.
echo ============================================================
echo CONCLUIDO COM SUCESSO
echo Resultado: %DEST%
echo ============================================================
start "" "%DEST%"
pause
exit /b 0

:erro
echo.
echo ============================================================
echo FALHA AO GERAR O NOVO ERP
echo Codigo de erro: %RC%
echo Nenhum banco final deve ser considerado valido.
echo Confira as mensagens acima.
echo ============================================================
pause
exit /b %RC%
