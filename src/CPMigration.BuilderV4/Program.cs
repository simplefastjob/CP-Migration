using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

Console.OutputEncoding = Encoding.UTF8;
var root = Directory.GetCurrentDirectory();
var sourcePath = Arg("--source") ?? Path.Combine(root, "output", "erp_intermediario.sqlite");
var outputDir = Arg("--output") ?? Path.Combine(root, "NovoERP-V4");
Directory.CreateDirectory(outputDir);

var finalPath = Path.Combine(outputDir, "NovoERP_V4.sqlite");
var partialPath = finalPath + ".partial";
var logPath = Path.Combine(outputDir, "BUILDER_V4.log");
var reportPath = Path.Combine(outputDir, "RELATORIO_BUILDER_V4.json");
var reviewPath = Path.Combine(outputDir, "REVISAO_V4.csv");

await using var log = new StreamWriter(logPath, false, new UTF8Encoding(true)) { AutoFlush = true };
await using var review = new StreamWriter(reviewPath, false, new UTF8Encoding(true)) { AutoFlush = true };
await review.WriteLineAsync("modulo;tabela;chave;motivo");
var report = new BuildReport { StartedAt = DateTimeOffset.Now, Source = sourcePath, Output = finalPath };
var totalWatch = Stopwatch.StartNew();

try
{
    if (!File.Exists(sourcePath)) throw new FileNotFoundException("Banco intermediário não encontrado.", sourcePath);
    if (File.Exists(partialPath)) File.Delete(partialPath);

    await using var source = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly;Cache=Private;Pooling=False");
    await using var target = new SqliteConnection($"Data Source={partialPath};Mode=ReadWriteCreate;Cache=Private;Pooling=False");
    await source.OpenAsync();
    await target.OpenAsync();
    await Exec(source, "PRAGMA query_only=ON; PRAGMA temp_store=MEMORY;");
    await Exec(target, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=OFF; PRAGMA temp_store=MEMORY;");
    await CreateSchema(target);
    var tables = await ReadTables(source);

    await Run("CLIENTES", BuildClients);
    await Run("PRODUTOS", BuildProducts);
    await Run("PRECOS", BuildPrices);
    await Run("APLICACOES", BuildApplications);
    await Run("ESTOQUE", BuildStock);
    await Run("FORNECEDORES", BuildSuppliers);
    await Run("VALIDACAO", Validate);

    await Exec(target, "PRAGMA optimize; PRAGMA wal_checkpoint(TRUNCATE);");
    await target.CloseAsync();
    await source.CloseAsync();
    SqliteConnection.ClearAllPools();
    if (File.Exists(finalPath)) File.Delete(finalPath);
    File.Move(partialPath, finalPath);

    report.Success = true;
    report.FinishedAt = DateTimeOffset.Now;
    report.Duration = totalWatch.Elapsed.ToString("c");
    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    Console.WriteLine($"Builder V4 concluído: {finalPath}");
}
catch (Exception ex)
{
    report.Success = false;
    report.FatalError = ex.ToString();
    report.FinishedAt = DateTimeOffset.Now;
    report.Duration = totalWatch.Elapsed.ToString("c");
    await Log("FATAL", ex.ToString());
    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}

async Task Run(string name, Func<Task<ModuleResult>> action)
{
    var sw = Stopwatch.StartNew();
    await Log("START", name);
    var result = await action();
    result.Duration = sw.Elapsed.ToString("c");
    report.Modules[name] = result;
    await Log("DONE", $"{name}: lidos={result.Read:N0}; gravados={result.Written:N0}; revisão={result.Review:N0}; tempo={sw.Elapsed:hh\\:mm\\:ss}");
}

async Task<ModuleResult> BuildClients()
{
    const string table = "T_RRPARTS__PAR_ENTIDADES";
    if (!tables.Contains(table)) return Missing(table);
    var result = new ModuleResult();
    await using var tx = (SqliteTransaction)await target.BeginTransactionAsync();
    await using var r = await SelectAll(source, table);
    var o = Ordinals(r);
    while (await r.ReadAsync())
    {
        result.Read++;
        var id = V(r, o, "PARENTD_ID", "ID");
        if (Blank(id)) { result.Review++; await Review("CLIENTES", table, "", "Sem chave"); continue; }
        var nome = V(r, o, "NOME_RAZAO_SOCIAL", "NOME_RAZAO", "NOME_ENTIDADE", "NOME", "DESC_ENTIDADE");
        var fantasia = V(r, o, "NOME_FANTASIA", "DESC_FANTASIA");
        if (Blank(nome)) nome = fantasia;
        await Insert(target, tx, """
INSERT OR REPLACE INTO clientes(
id,codigo,nome,fantasia,tipo_pessoa,cpf_cnpj,ie,im,email,telefone,cep,endereco,numero,bairro,cidade,uf,
limite_credito,saldo,data_cadastro,ultima_compra,ultima_venda,ativo,raw_json)
VALUES(@id,@codigo,@nome,@fantasia,@tipo,@doc,@ie,@im,@email,@fone,@cep,@end,@num,@bairro,@cidade,@uf,
@limite,@saldo,@cad,@compra,@venda,@ativo,@raw)
""", new()
        {
            ["@id"] = id, ["@codigo"] = V(r,o,"CODG_ENTIDADE","CODIGO"), ["@nome"] = nome,
            ["@fantasia"] = fantasia, ["@tipo"] = V(r,o,"INDR_PESSOA","TIPO_PESSOA"),
            ["@doc"] = V(r,o,"NUMR_CGC","NUMR_CPF","CNPJ","CPF"), ["@ie"] = V(r,o,"NUMR_INSCRICAO_ESTADUAL","INSCRICAO_ESTADUAL","IE"),
            ["@im"] = V(r,o,"NUMR_INSCRICAO_MUNICIPAL","INSCRICAO_MUNICIPAL","IM"), ["@email"] = V(r,o,"NOME_EMAIL","DESC_EMAIL","EMAIL"),
            ["@fone"] = V(r,o,"NUMR_TELEFONE","NUMR_FONE","TELEFONE","FONE"), ["@cep"] = V(r,o,"NUMR_CEP","CEP"),
            ["@end"] = V(r,o,"NOME_LOGRADOURO","DESC_ENDERECO","ENDERECO"), ["@num"] = V(r,o,"NUMR_ENDERECO","NUMERO"),
            ["@bairro"] = V(r,o,"NOME_BAIRRO","BAIRRO"), ["@cidade"] = V(r,o,"NOME_CIDADE","CIDADE"), ["@uf"] = V(r,o,"SIGL_UF","UF"),
            ["@limite"] = D(r,o,"VALR_LIMITE","VALR_LIMITE_CREDITO"), ["@saldo"] = D(r,o,"VALR_SALDO"),
            ["@cad"] = V(r,o,"DATA_CADASTRO","DTHR_CADASTRO"), ["@compra"] = V(r,o,"DATA_ULTIMA_COMPRA"), ["@venda"] = V(r,o,"DATA_ULTIMA_VENDA"),
            ["@ativo"] = V(r,o,"INDR_ATIVO","INDR_INATIVO"), ["@raw"] = RowJson(r)
        });
        result.Written++;
    }
    await tx.CommitAsync();
    await EnrichPersons(result);
    return result;
}

async Task EnrichPersons(ModuleResult result)
{
    foreach (var item in new[] { ("T_RRPARTS__PAR_PESSOA_FISICA","PF"), ("T_RRPARTS__PAR_PESSOA_JURIDICA","PJ") })
    {
        if (!tables.Contains(item.Item1)) continue;
        await using var tx = (SqliteTransaction)await target.BeginTransactionAsync();
        await using var r = await SelectAll(source, item.Item1);
        var o = Ordinals(r);
        while (await r.ReadAsync())
        {
            var id = V(r,o,"PARENTD_ID"); if (Blank(id)) continue;
            var nome = item.Item2 == "PF" ? V(r,o,"NOME_PESSOA","NOME_COMPLETO","NOME") : V(r,o,"NOME_RAZAO_SOCIAL","RAZAO_SOCIAL","NOME");
            var fantasia = item.Item2 == "PJ" ? V(r,o,"NOME_FANTASIA","FANTASIA") : null;
            await Insert(target, tx, """
UPDATE clientes SET tipo_pessoa=@tipo,
nome=COALESCE(NULLIF(@nome,''),nome), fantasia=COALESCE(NULLIF(@fantasia,''),fantasia),
ie=COALESCE(NULLIF(@ie,''),ie), im=COALESCE(NULLIF(@im,''),im), complemento_json=@raw WHERE id=@id
""", new() { ["@tipo"] = item.Item2, ["@nome"] = nome, ["@fantasia"] = fantasia, ["@ie"] = V(r,o,"NUMR_INSCRICAO_ESTADUAL","IE"), ["@im"] = V(r,o,"NUMR_INSCRICAO_MUNICIPAL","IM"), ["@raw"] = RowJson(r), ["@id"] = id });
        }
        await tx.CommitAsync();
    }
}

async Task<ModuleResult> BuildProducts()
{
    const string table = "T_RRPARTS__COM_PRODUTOS";
    if (!tables.Contains(table)) return Missing(table);
    var result = new ModuleResult();
    await using var tx = (SqliteTransaction)await target.BeginTransactionAsync();
    await using var r = await SelectAll(source, table);
    var o = Ordinals(r);
    while (await r.ReadAsync())
    {
        result.Read++;
        var id = V(r,o,"PCIPMAT_ID","COMPROD_ID","ID");
        if (Blank(id)) { result.Review++; await Review("PRODUTOS",table,"","Sem chave"); continue; }
        await Insert(target, tx, """
INSERT OR REPLACE INTO produtos(id,comprod_id,codigo,codigo_original,codigo_fabricante,descricao,descricao_reduzida,
marca_id,grupo_id,subgrupo_id,ncm,cest,eain,unidade,peso,origem,ativo,raw_json)
VALUES(@id,@comprod,@codigo,@original,@fabricante,@descricao,@reduzida,@marca,@grupo,@subgrupo,@ncm,@cest,@ean,@unidade,@peso,@origem,@ativo,@raw)
""", new()
        {
            ["@id"] = id, ["@comprod"] = V(r,o,"COMPROD_ID"), ["@codigo"] = V(r,o,"CODG_PRODUTO","CODIGO","CODG_ITEM"),
            ["@original"] = V(r,o,"CODG_ORIGINAL","CODIGO_ORIGINAL"), ["@fabricante"] = V(r,o,"CODG_FABRICANTE","CODIGO_FABRICANTE"),
            ["@descricao"] = V(r,o,"DESC_PRODUTO","DESCRICAO","NOME_PRODUTO"), ["@reduzida"] = V(r,o,"DESC_REDUZIDA","DESC_RESUMIDA"),
            ["@marca"] = V(r,o,"COMMARC_ID","MARCA_ID"), ["@grupo"] = V(r,o,"COMGRUP_ID","GRUPO_ID"), ["@subgrupo"] = V(r,o,"COMSGRP_ID","SUBGRUPO_ID"),
            ["@ncm"] = V(r,o,"CODG_NCM","NCM"), ["@cest"] = V(r,o,"CODG_CEST","CEST"), ["@ean"] = V(r,o,"CODG_EAN","CODG_GTIN","GTIN","EAN","CODG_BARRAS"),
            ["@unidade"] = V(r,o,"CODG_UNIDADE","UNIDADE"), ["@peso"] = D(r,o,"PESO_LIQUIDO","VALR_PESO","PESO"), ["@origem"] = V(r,o,"CODG_ORIGEM","ORIGEM"),
            ["@ativo"] = V(r,o,"INDR_ATIVO","INDR_INATIVO"), ["@raw"] = RowJson(r)
        });
        result.Written++;
    }
    await tx.CommitAsync();
    await CopyCodesAndSimilars();
    return result;
}

async Task CopyCodesAndSimilars()
{
    if (tables.Contains("T_RRPARTS__COM_PRODUTO_CODIGO"))
    {
        await using var tx = (SqliteTransaction)await target.BeginTransactionAsync();
        await using var r = await SelectAll(source,"T_RRPARTS__COM_PRODUTO_CODIGO"); var o = Ordinals(r);
        while (await r.ReadAsync()) await Insert(target,tx,"INSERT INTO produto_codigos(produto_id,codigo,tipo,raw_json) VALUES(@p,@c,@t,@r)",new(){["@p"]=V(r,o,"COMPROD_ID"),["@c"]=V(r,o,"CODG_PRODUTO","CODIGO","CODG_BARRAS"),["@t"]=V(r,o,"INDR_TIPO","TIPO_CODIGO"),["@r"]=RowJson(r)});
        await tx.CommitAsync();
    }
    if (tables.Contains("T_RRPARTS__COM_PRODUTO_SIMILAR"))
    {
        await using var tx = (SqliteTransaction)await target.BeginTransactionAsync();
        await using var r = await SelectAll(source,"T_RRPARTS__COM_PRODUTO_SIMILAR"); var o = Ordinals(r);
        while (await r.ReadAsync()) await Insert(target,tx,"INSERT INTO produto_similares(produto_id,produto_similar_id,raw_json) VALUES(@p,@s,@r)",new(){["@p"]=V(r,o,"COMPROD_ID"),["@s"]=V(r,o,"COMPROD_ID_SIMILAR"),["@r"]=RowJson(r)});
        await tx.CommitAsync();
    }
}

async Task<ModuleResult> BuildPrices()
{
    var result = new ModuleResult();
    foreach (var item in new[] { ("T_RRPARTS__COM_PRECO_PRODUTO_ATUAL",false), ("T_RRPARTS__COM_PRECO_PRODUTO",true) })
    {
        if (!tables.Contains(item.Item1)) continue;
        await using var tx = (SqliteTransaction)await target.BeginTransactionAsync();
        await using var r = await SelectAll(source,item.Item1); var o = Ordinals(r);
        while (await r.ReadAsync())
        {
            result.Read++;
            await Insert(target,tx,"""
INSERT INTO produto_precos(produto_id,tabela_preco_id,custo,preco_venda,preco_minimo,preco_maximo,margem,inicio_vigencia,fim_vigencia,historico,raw_json)
VALUES(@p,@t,@c,@v,@min,@max,@m,@ini,@fim,@h,@r)
""",new(){["@p"]=V(r,o,"COMPROD_ID","PCIPMAT_ID"),["@t"]=V(r,o,"COMLSPR_ID","TABELA_PRECO_ID"),["@c"]=D(r,o,"VALR_CUSTO","VALR_ULTIMO_CUSTO"),["@v"]=D(r,o,"VALR_PRECO","VALR_VENDA","VALR_PRECO_VENDA"),["@min"]=D(r,o,"VALR_PRECO_MINIMO","VALR_MINIMO"),["@max"]=D(r,o,"VALR_PRECO_MAXIMO","VALR_MAXIMO"),["@m"]=D(r,o,"PERC_MARGEM","MARGEM"),["@ini"]=V(r,o,"DATA_INICIAL","DATA_VIGENCIA","DTHR_INICIO"),["@fim"]=V(r,o,"DATA_FINAL","DTHR_FIM"),["@h"]=item.Item2?1:0,["@r"]=RowJson(r)});
            result.Written++;
        }
        await tx.CommitAsync();
    }
    return result;
}

async Task<ModuleResult> BuildApplications()
{
    var result = new ModuleResult();
    foreach (var table in new[] { "T_RRPARTS__PCI_APLICACAO_RESUMO", "T_RRPARTS__PCI_APLICACAO_PESQUISA" })
    {
        if (!tables.Contains(table)) continue;
        await using var tx = (SqliteTransaction)await target.BeginTransactionAsync();
        await using var r = await SelectAll(source,table); var o = Ordinals(r);
        while (await r.ReadAsync())
        {
            result.Read++;
            await Insert(target,tx,"""
INSERT INTO produto_aplicacoes(produto_id,marca_veiculo,modelo,motor,ano_inicial,ano_final,descricao,raw_json)
VALUES(@p,@marca,@modelo,@motor,@ai,@af,@d,@r)
""",new(){["@p"]=V(r,o,"COMPROD_ID","PCIPMAT_ID"),["@marca"]=V(r,o,"NOME_MARCA","MARCA_VEICULO"),["@modelo"]=V(r,o,"NOME_MODELO","MODELO"),["@motor"]=V(r,o,"DESC_MOTOR","MOTOR"),["@ai"]=V(r,o,"ANO_INICIAL","ANO_DE"),["@af"]=V(r,o,"ANO_FINAL","ANO_ATE"),["@d"]=V(r,o,"DESC_APLICACAO","APLICACAO"),["@r"]=RowJson(r)});
            result.Written++;
        }
        await tx.CommitAsync();
    }
    return result;
}

async Task<ModuleResult> BuildStock()
{
    const string table = "T_RRPARTS__PCI_ITEM_DEPOSITO";
    if (!tables.Contains(table)) return Missing(table);
    var result = new ModuleResult();
    await using var tx = (SqliteTransaction)await target.BeginTransactionAsync();
    await using var r = await SelectAll(source,table); var o = Ordinals(r);
    while (await r.ReadAsync())
    {
        result.Read++;
        var p = V(r,o,"PCIPMAT_ID");
        if (Blank(p)) { result.Review++; await Review("ESTOQUE",table,"","Sem produto"); continue; }
        await Insert(target,tx,"""
INSERT INTO estoque(produto_id,empresa_id,deposito_id,quantidade_fisica,quantidade_reservada,quantidade_disponivel,
custo_medio,ultimo_custo,data_ultima_compra,data_ultima_venda,raw_json)
VALUES(@p,@e,@d,@qf,@qr,@qd,@cm,@uc,@compra,@venda,@r)
""",new(){["@p"]=p,["@e"]=V(r,o,"PAREMPR_ID"),["@d"]=V(r,o,"PCIDEPO_ID"),["@qf"]=D(r,o,"QTDE_ESTOQUE_FISICO","QTDE_ESTOQUE","QTDE_SALDO","QTDE_ATUAL"),["@qr"]=D(r,o,"QTDE_ESTOQUE_RESERVADO","QTDE_RESERVADA","QTDE_RESERVADO"),["@qd"]=D(r,o,"QTDE_ESTOQUE_DISPONIVEL","QTDE_DISPONIVEL"),["@cm"]=D(r,o,"VALR_CUSTO_MEDIO","VALR_CUSTO_HISTORICO","VALR_CUSTO"),["@uc"]=D(r,o,"VALR_ULTIMO_CUSTO"),["@compra"]=V(r,o,"DATA_ULTIMA_COMPRA"),["@venda"]=V(r,o,"DATA_ULTIMA_VENDA"),["@r"]=RowJson(r)});
        result.Written++;
    }
    await tx.CommitAsync();
    await BuildLocations();
    return result;
}

async Task BuildLocations()
{
    const string table = "T_RRPARTS__PCI_LOCACAO"; if (!tables.Contains(table)) return;
    await using var tx = (SqliteTransaction)await target.BeginTransactionAsync();
    await using var r = await SelectAll(source,table); var o = Ordinals(r);
    while (await r.ReadAsync()) await Insert(target,tx,"""
INSERT INTO localizacoes(produto_id,empresa_id,deposito_id,rua,prateleira,coluna,nivel,posicao,quantidade,raw_json)
VALUES(@p,@e,@d,@rua,@prat,@col,@niv,@pos,@q,@r)
""",new(){["@p"]=V(r,o,"PCIPMAT_ID"),["@e"]=V(r,o,"PAREMPR_ID"),["@d"]=V(r,o,"PCIDEPO_ID"),["@rua"]=V(r,o,"CODG_RUA","RUA"),["@prat"]=V(r,o,"CODG_PRATELEIRA","PRATELEIRA"),["@col"]=V(r,o,"CODG_COLUNA","COLUNA"),["@niv"]=V(r,o,"CODG_NIVEL","NIVEL"),["@pos"]=V(r,o,"CODG_POSICAO","POSICAO","DESC_LOCACAO"),["@q"]=D(r,o,"QTDE_LOCACAO","QTDE_ESTOQUE"),["@r"]=RowJson(r)});
    await tx.CommitAsync();
}

async Task<ModuleResult> BuildSuppliers()
{
    var result = new ModuleResult();
    if (tables.Contains("T_RRPARTS__PAR_FORN_ENTIDADE"))
    {
        await using var tx = (SqliteTransaction)await target.BeginTransactionAsync();
        await using var r = await SelectAll(source,"T_RRPARTS__PAR_FORN_ENTIDADE"); var o = Ordinals(r);
        while (await r.ReadAsync()) { result.Read++; var id=V(r,o,"PARENTD_ID"); if(Blank(id)) continue; await Insert(target,tx,"INSERT OR REPLACE INTO fornecedores(id,cliente_id,ativo,raw_json) VALUES(@id,@c,@a,@r)",new(){["@id"]=id,["@c"]=id,["@a"]=V(r,o,"INDR_ATIVO"),["@r"]=RowJson(r)}); result.Written++; }
        await tx.CommitAsync();
    }
    if (tables.Contains("T_RRPARTS__PCI_FORNECEDOR_PRODUTO"))
    {
        await using var tx = (SqliteTransaction)await target.BeginTransactionAsync();
        await using var r = await SelectAll(source,"T_RRPARTS__PCI_FORNECEDOR_PRODUTO"); var o = Ordinals(r);
        while (await r.ReadAsync()) await Insert(target,tx,"INSERT INTO produto_fornecedores(produto_id,fornecedor_id,codigo_fornecedor,custo,principal,raw_json) VALUES(@p,@f,@c,@v,@pr,@r)",new(){["@p"]=V(r,o,"PCIPMAT_ID","COMPROD_ID"),["@f"]=V(r,o,"PARENTD_ID"),["@c"]=V(r,o,"CODG_PRODUTO_FORNECEDOR"),["@v"]=D(r,o,"VALR_CUSTO","VALR_ULTIMO_CUSTO"),["@pr"]=V(r,o,"INDR_PRINCIPAL"),["@r"]=RowJson(r)});
        await tx.CommitAsync();
    }
    return result;
}

async Task<ModuleResult> Validate()
{
    var result = new ModuleResult();
    var checks = new Dictionary<string,string>
    {
        ["clientes_sem_nome"] = "SELECT COUNT(*) FROM clientes WHERE COALESCE(TRIM(nome),'')=''",
        ["clientes_sem_documento"] = "SELECT COUNT(*) FROM clientes WHERE COALESCE(TRIM(cpf_cnpj),'')=''",
        ["produtos_sem_codigo"] = "SELECT COUNT(*) FROM produtos WHERE COALESCE(TRIM(codigo),'')=''",
        ["produtos_sem_descricao"] = "SELECT COUNT(*) FROM produtos WHERE COALESCE(TRIM(descricao),'')=''",
        ["estoque_sem_produto"] = "SELECT COUNT(*) FROM estoque e LEFT JOIN produtos p ON p.id=e.produto_id WHERE p.id IS NULL",
        ["precos_sem_produto"] = "SELECT COUNT(*) FROM produto_precos e LEFT JOIN produtos p ON p.comprod_id=e.produto_id OR p.id=e.produto_id WHERE p.id IS NULL"
    };
    foreach (var c in checks) report.Validation[c.Key] = await Scalar(target,c.Value);
    result.Read = checks.Count; result.Written = checks.Count;
    return result;
}

async Task CreateSchema(SqliteConnection c) => await Exec(c,"""
CREATE TABLE clientes(id TEXT PRIMARY KEY,codigo TEXT,nome TEXT,fantasia TEXT,tipo_pessoa TEXT,cpf_cnpj TEXT,ie TEXT,im TEXT,email TEXT,telefone TEXT,cep TEXT,endereco TEXT,numero TEXT,bairro TEXT,cidade TEXT,uf TEXT,limite_credito REAL,saldo REAL,data_cadastro TEXT,ultima_compra TEXT,ultima_venda TEXT,ativo TEXT,raw_json TEXT,complemento_json TEXT);
CREATE TABLE fornecedores(id TEXT PRIMARY KEY,cliente_id TEXT,ativo TEXT,raw_json TEXT);
CREATE TABLE produtos(id TEXT PRIMARY KEY,comprod_id TEXT,codigo TEXT,codigo_original TEXT,codigo_fabricante TEXT,descricao TEXT,descricao_reduzida TEXT,marca_id TEXT,grupo_id TEXT,subgrupo_id TEXT,ncm TEXT,cest TEXT,eain TEXT,unidade TEXT,peso REAL,origem TEXT,ativo TEXT,raw_json TEXT);
CREATE TABLE produto_codigos(id INTEGER PRIMARY KEY AUTOINCREMENT,produto_id TEXT,codigo TEXT,tipo TEXT,raw_json TEXT);
CREATE TABLE produto_similares(id INTEGER PRIMARY KEY AUTOINCREMENT,produto_id TEXT,produto_similar_id TEXT,raw_json TEXT);
CREATE TABLE produto_precos(id INTEGER PRIMARY KEY AUTOINCREMENT,produto_id TEXT,tabela_preco_id TEXT,custo REAL,preco_venda REAL,preco_minimo REAL,preco_maximo REAL,margem REAL,inicio_vigencia TEXT,fim_vigencia TEXT,historico INTEGER,raw_json TEXT);
CREATE TABLE produto_aplicacoes(id INTEGER PRIMARY KEY AUTOINCREMENT,produto_id TEXT,marca_veiculo TEXT,modelo TEXT,motor TEXT,ano_inicial TEXT,ano_final TEXT,descricao TEXT,raw_json TEXT);
CREATE TABLE produto_fornecedores(id INTEGER PRIMARY KEY AUTOINCREMENT,produto_id TEXT,fornecedor_id TEXT,codigo_fornecedor TEXT,custo REAL,principal TEXT,raw_json TEXT);
CREATE TABLE estoque(id INTEGER PRIMARY KEY AUTOINCREMENT,produto_id TEXT,empresa_id TEXT,deposito_id TEXT,quantidade_fisica REAL,quantidade_reservada REAL,quantidade_disponivel REAL,custo_medio REAL,ultimo_custo REAL,data_ultima_compra TEXT,data_ultima_venda TEXT,raw_json TEXT);
CREATE TABLE localizacoes(id INTEGER PRIMARY KEY AUTOINCREMENT,produto_id TEXT,empresa_id TEXT,deposito_id TEXT,rua TEXT,prateleira TEXT,coluna TEXT,nivel TEXT,posicao TEXT,quantidade REAL,raw_json TEXT);
CREATE INDEX idx_produtos_comprod ON produtos(comprod_id); CREATE INDEX idx_estoque_produto ON estoque(produto_id); CREATE INDEX idx_precos_produto ON produto_precos(produto_id); CREATE INDEX idx_aplicacoes_produto ON produto_aplicacoes(produto_id); CREATE INDEX idx_clientes_doc ON clientes(cpf_cnpj);
""");

async Task<HashSet<string>> ReadTables(SqliteConnection c){var set=new HashSet<string>(StringComparer.OrdinalIgnoreCase);await using var cmd=c.CreateCommand();cmd.CommandText="SELECT name FROM sqlite_master WHERE type='table'";await using var r=await cmd.ExecuteReaderAsync();while(await r.ReadAsync())set.Add(r.GetString(0));return set;}
async Task<SqliteDataReader> SelectAll(SqliteConnection c,string table){var cmd=c.CreateCommand();cmd.CommandText=$"SELECT * FROM {Q(table)}";return await cmd.ExecuteReaderAsync();}
Dictionary<string,int> Ordinals(SqliteDataReader r){var d=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);for(int i=0;i<r.FieldCount;i++)d[r.GetName(i)]=i;return d;}
string? V(SqliteDataReader r,Dictionary<string,int> o,params string[] names){foreach(var n in names)if(o.TryGetValue(n,out var i)&&!r.IsDBNull(i)){var s=Convert.ToString(r.GetValue(i),CultureInfo.InvariantCulture)?.Trim();if(!Blank(s))return s;}return null;}
object? D(SqliteDataReader r,Dictionary<string,int> o,params string[] names){var s=V(r,o,names);if(Blank(s))return DBNull.Value;s=s!.Replace(".","").Replace(",",".");return decimal.TryParse(s,NumberStyles.Any,CultureInfo.InvariantCulture,out var d)?d:DBNull.Value;}
string RowJson(SqliteDataReader r){var d=new Dictionary<string,object?>();for(int i=0;i<r.FieldCount;i++)d[r.GetName(i)]=r.IsDBNull(i)?null:r.GetValue(i);return JsonSerializer.Serialize(d);}
async Task Insert(SqliteConnection c,SqliteTransaction tx,string sql,Dictionary<string,object?> p){await using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;foreach(var x in p)cmd.Parameters.AddWithValue(x.Key,x.Value??DBNull.Value);await cmd.ExecuteNonQueryAsync();}
async Task Exec(SqliteConnection c,string sql){await using var cmd=c.CreateCommand();cmd.CommandText=sql;await cmd.ExecuteNonQueryAsync();}
async Task<long> Scalar(SqliteConnection c,string sql){await using var cmd=c.CreateCommand();cmd.CommandText=sql;return Convert.ToInt64(await cmd.ExecuteScalarAsync()??0);}
async Task Log(string code,string msg){await log.WriteLineAsync($"{DateTimeOffset.Now:O} [{code}] {msg}");}
async Task Review(string m,string t,string k,string motivo){await review.WriteLineAsync($"{Csv(m)};{Csv(t)};{Csv(k)};{Csv(motivo)}");}
string Csv(string s)=>'"'+s.Replace("\"","\"\"")+'"'; string Q(string s)=>'"'+s.Replace("\"","\"\"")+'"'; bool Blank(string? s)=>string.IsNullOrWhiteSpace(s);
string? Arg(string n){var i=Array.FindIndex(args,x=>x.Equals(n,StringComparison.OrdinalIgnoreCase));return i>=0&&i+1<args.Length?Path.GetFullPath(args[i+1]):null;}
ModuleResult Missing(string table)=>new(){Notes=$"Tabela ausente: {table}"};
sealed class ModuleResult{public long Read{get;set;}public long Written{get;set;}public long Review{get;set;}public string? Duration{get;set;}public string? Notes{get;set;}}
sealed class BuildReport{public DateTimeOffset StartedAt{get;set;}public DateTimeOffset FinishedAt{get;set;}public string Source{get;set;}="";public string Output{get;set;}="";public string? Duration{get;set;}public bool Success{get;set;}public string? FatalError{get;set;}public Dictionary<string,ModuleResult> Modules{get;set;}=[];public Dictionary<string,long> Validation{get;set;}=[];}
