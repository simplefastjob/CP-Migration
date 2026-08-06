using Microsoft.Data.Sqlite;
using CPMigration.BuilderV4.Core;

namespace CPMigration.BuilderV4.Builders;

public sealed class ClienteBuilder : IBuilderModule
{
    public string Name => "CLIENTES";
    public async Task<ModuleResult> ExecuteAsync(BuilderContext c, CancellationToken ct)
    {
        const string table = "T_RRPARTS__PAR_ENTIDADES";
        var result = new ModuleResult();
        if (!c.HasTable(table)) return result;
        await using var tx = (SqliteTransaction)await c.Target.BeginTransactionAsync(ct);
        await using var r = await c.ReadAllAsync(table, ct);
        var o = BuilderContext.Ordinals(r);
        while (await r.ReadAsync(ct))
        {
            result.Read++;
            var id = BuilderContext.Text(r,o,"PARENTD_ID","ID");
            if (id is null) { result.Review++; await c.ReviewAsync(Name,table,"","Sem PARENTD_ID"); continue; }
            await c.ExecuteAsync("""
INSERT OR REPLACE INTO clientes(id,nome,fantasia,tipo_pessoa,cpf_cnpj,ie,im,email,telefone,cep,endereco,numero,bairro,cidade,uf,limite_credito,saldo,data_cadastro,ultima_compra,ultima_venda,raw_json)
VALUES(@id,@nome,@fantasia,@tipo,@doc,@ie,@im,@email,@fone,@cep,@end,@num,@bairro,@cidade,@uf,@limite,@saldo,@cad,@compra,@venda,@raw)
""", new Dictionary<string,object?>
            {
                ["@id"]=id,
                ["@nome"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"NOME_RAZAO_SOCIAL","NOME_RAZAO","NOME_ENTIDADE","NOME","DESC_ENTIDADE")),
                ["@fantasia"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"NOME_FANTASIA","DESC_FANTASIA")),
                ["@tipo"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"INDR_PESSOA","TIPO_PESSOA")),
                ["@doc"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"NUMR_CGC","NUMR_CPF","CNPJ","CPF")),
                ["@ie"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"NUMR_INSCRICAO_ESTADUAL","NUMR_IE","IE")),
                ["@im"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"NUMR_INSCRICAO_MUNICIPAL","NUMR_IM","IM")),
                ["@email"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"NOME_EMAIL","DESC_EMAIL","EMAIL")),
                ["@fone"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"NUMR_TELEFONE","NUMR_FONE","TELEFONE","FONE")),
                ["@cep"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"NUMR_CEP","CEP")),
                ["@end"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"NOME_LOGRADOURO","DESC_ENDERECO","ENDERECO")),
                ["@num"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"NUMR_ENDERECO","NUMERO")),
                ["@bairro"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"NOME_BAIRRO","BAIRRO")),
                ["@cidade"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"NOME_CIDADE","CIDADE")),
                ["@uf"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"SIGL_UF","UF")),
                ["@limite"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"VALR_LIMITE","VALR_LIMITE_CREDITO")),
                ["@saldo"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"VALR_SALDO")),
                ["@cad"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"DATA_CADASTRO","DTHR_CADASTRO")),
                ["@compra"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"DATA_ULTIMA_COMPRA")),
                ["@venda"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"DATA_ULTIMA_VENDA")),
                ["@raw"]=BuilderContext.RowJson(r)
            },tx,ct);
            result.Written++;
        }
        await tx.CommitAsync(ct);
        return result;
    }
}

public sealed class ProdutoBuilder : IBuilderModule
{
    public string Name => "PRODUTOS";
    public async Task<ModuleResult> ExecuteAsync(BuilderContext c, CancellationToken ct)
    {
        const string table = "T_RRPARTS__COM_PRODUTOS";
        var result = new ModuleResult();
        if (!c.HasTable(table)) return result;
        await using (var tx = (SqliteTransaction)await c.Target.BeginTransactionAsync(ct))
        {
            await using var r = await c.ReadAllAsync(table,ct); var o=BuilderContext.Ordinals(r);
            while(await r.ReadAsync(ct))
            {
                result.Read++;
                var id=BuilderContext.Text(r,o,"PCIPMAT_ID","COMPROD_ID","ID");
                if(id is null){result.Review++;await c.ReviewAsync(Name,table,"","Sem ID do produto");continue;}
                await c.ExecuteAsync("""
INSERT OR REPLACE INTO produtos(id,comprod_id,codigo,codigo_original,codigo_fabricante,descricao,descricao_reduzida,marca_id,grupo_id,subgrupo_id,ncm,cest,ean,unidade,peso,origem,ativo,raw_json)
VALUES(@id,@comprod,@codigo,@original,@fabricante,@descricao,@reduzida,@marca,@grupo,@subgrupo,@ncm,@cest,@ean,@unidade,@peso,@origem,@ativo,@raw)
""",new Dictionary<string,object?>
                {
                    ["@id"]=id,["@comprod"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"COMPROD_ID")),
                    ["@codigo"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"CODG_PRODUTO","CODIGO","CODG_ITEM")),
                    ["@original"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"CODG_ORIGINAL","CODIGO_ORIGINAL")),
                    ["@fabricante"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"CODG_FABRICANTE","CODIGO_FABRICANTE")),
                    ["@descricao"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"DESC_PRODUTO","DESCRICAO","NOME_PRODUTO")),
                    ["@reduzida"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"DESC_REDUZIDA","DESC_RESUMIDA")),
                    ["@marca"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"COMMARC_ID","MARCA_ID")),
                    ["@grupo"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"COMGRUP_ID","GRUPO_ID")),
                    ["@subgrupo"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"COMSGRU_ID","SUBGRUPO_ID")),
                    ["@ncm"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"CODG_NCM","NCM")),
                    ["@cest"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"CODG_CEST","CEST")),
                    ["@ean"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"CODG_EAN","GTIN","EAN","CODG_BARRAS")),
                    ["@unidade"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"CODG_UNIDADE","UNIDADE")),
                    ["@peso"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"PESO_LIQUIDO","PESO_BRUTO","PESO")),
                    ["@origem"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"CODG_ORIGEM","ORIGEM")),
                    ["@ativo"]=BuilderContext.DbValue(BuilderContext.Text(r,o,"INDR_ATIVO","INDR_INATIVO")),
                    ["@raw"]=BuilderContext.RowJson(r)
                },tx,ct); result.Written++;
            }
            await tx.CommitAsync(ct);
        }
        await CopyCodes(c,ct); await CopyPrices(c,ct,false); await CopyPrices(c,ct,true); await CopyApplications(c,ct); await CopySimilar(c,ct); await CopyProductSuppliers(c,ct);
        return result;
    }

    private static async Task CopyCodes(BuilderContext c,CancellationToken ct)
    {
        const string t="T_RRPARTS__COM_PRODUTO_CODIGO"; if(!c.HasTable(t))return;
        await using var tx=(SqliteTransaction)await c.Target.BeginTransactionAsync(ct); await using var r=await c.ReadAllAsync(t,ct);var o=BuilderContext.Ordinals(r);
        while(await r.ReadAsync(ct)) await c.ExecuteAsync("INSERT INTO produto_codigos(produto_id,codigo,tipo,raw_json) VALUES(@p,@c,@t,@r)",new Dictionary<string,object?>{{"@p",BuilderContext.DbValue(BuilderContext.Text(r,o,"COMPROD_ID"))},{"@c",BuilderContext.DbValue(BuilderContext.Text(r,o,"CODG_PRODUTO","CODIGO","CODG_BARRAS"))},{"@t",BuilderContext.DbValue(BuilderContext.Text(r,o,"TIPO_CODIGO","INDR_TIPO"))},{"@r",BuilderContext.RowJson(r)}},tx,ct);
        await tx.CommitAsync(ct);
    }
    private static async Task CopyPrices(BuilderContext c,CancellationToken ct,bool history)
    {
        var t=history?"T_RRPARTS__COM_PRECO_PRODUTO":"T_RRPARTS__COM_PRECO_PRODUTO_ATUAL"; if(!c.HasTable(t))return;
        await using var tx=(SqliteTransaction)await c.Target.BeginTransactionAsync(ct); await using var r=await c.ReadAllAsync(t,ct);var o=BuilderContext.Ordinals(r);
        while(await r.ReadAsync(ct)) await c.ExecuteAsync("INSERT INTO produto_precos(produto_id,custo,venda,minimo,maximo,margem,tabela_id,vigencia_inicio,vigencia_fim,historico,raw_json) VALUES(@p,@c,@v,@min,@max,@m,@tab,@ini,@fim,@h,@r)",new Dictionary<string,object?>{{"@p",BuilderContext.DbValue(BuilderContext.Text(r,o,"COMPROD_ID","PCIPMAT_ID"))},{"@c",BuilderContext.DbValue(BuilderContext.Text(r,o,"VALR_CUSTO","VALR_ULTIMO_CUSTO"))},{"@v",BuilderContext.DbValue(BuilderContext.Text(r,o,"VALR_PRECO","VALR_VENDA"))},{"@min",BuilderContext.DbValue(BuilderContext.Text(r,o,"VALR_MINIMO","VALR_PRECO_MINIMO"))},{"@max",BuilderContext.DbValue(BuilderContext.Text(r,o,"VALR_MAXIMO","VALR_PRECO_MAXIMO"))},{"@m",BuilderContext.DbValue(BuilderContext.Text(r,o,"PERC_MARGEM","MARGEM"))},{"@tab",BuilderContext.DbValue(BuilderContext.Text(r,o,"COMLSPR_ID","TABELA_ID"))},{"@ini",BuilderContext.DbValue(BuilderContext.Text(r,o,"DATA_INICIAL","DATA_VIGENCIA"))},{"@fim",BuilderContext.DbValue(BuilderContext.Text(r,o,"DATA_FINAL"))},{"@h",history?1:0},{"@r",BuilderContext.RowJson(r)}},tx,ct);
        await tx.CommitAsync(ct);
    }
    private static async Task CopyApplications(BuilderContext c,CancellationToken ct)
    {
        const string t="T_RRPARTS__PCI_APLICACAO_RESUMO"; if(!c.HasTable(t))return;
        await using var tx=(SqliteTransaction)await c.Target.BeginTransactionAsync(ct); await using var r=await c.ReadAllAsync(t,ct);var o=BuilderContext.Ordinals(r);
        while(await r.ReadAsync(ct)) await c.ExecuteAsync("INSERT INTO produto_aplicacoes(produto_id,descricao,marca,modelo,motor,ano_inicial,ano_final,raw_json) VALUES(@p,@d,@ma,@mo,@mt,@ai,@af,@r)",new Dictionary<string,object?>{{"@p",BuilderContext.DbValue(BuilderContext.Text(r,o,"COMPROD_ID"))},{"@d",BuilderContext.DbValue(BuilderContext.Text(r,o,"DESC_APLICACAO","APLICACAO"))},{"@ma",BuilderContext.DbValue(BuilderContext.Text(r,o,"MARCA_VEICULO","DESC_MARCA"))},{"@mo",BuilderContext.DbValue(BuilderContext.Text(r,o,"MODELO","DESC_MODELO"))},{"@mt",BuilderContext.DbValue(BuilderContext.Text(r,o,"MOTOR","DESC_MOTOR"))},{"@ai",BuilderContext.DbValue(BuilderContext.Text(r,o,"ANO_INICIAL"))},{"@af",BuilderContext.DbValue(BuilderContext.Text(r,o,"ANO_FINAL"))},{"@r",BuilderContext.RowJson(r)}},tx,ct);
        await tx.CommitAsync(ct);
    }
    private static async Task CopySimilar(BuilderContext c,CancellationToken ct)
    {
        const string t="T_RRPARTS__COM_PRODUTO_SIMILAR"; if(!c.HasTable(t))return;
        await using var tx=(SqliteTransaction)await c.Target.BeginTransactionAsync(ct); await using var r=await c.ReadAllAsync(t,ct);var o=BuilderContext.Ordinals(r);
        while(await r.ReadAsync(ct)) await c.ExecuteAsync("INSERT INTO produto_similares(produto_id,produto_similar_id,raw_json) VALUES(@p,@s,@r)",new Dictionary<string,object?>{{"@p",BuilderContext.DbValue(BuilderContext.Text(r,o,"COMPROD_ID"))},{"@s",BuilderContext.DbValue(BuilderContext.Text(r,o,"COMPROD_ID_SIMILAR"))},{"@r",BuilderContext.RowJson(r)}},tx,ct);
        await tx.CommitAsync(ct);
    }
    private static async Task CopyProductSuppliers(BuilderContext c,CancellationToken ct)
    {
        const string t="T_RRPARTS__PCI_FORNECEDOR_PRODUTO"; if(!c.HasTable(t))return;
        await using var tx=(SqliteTransaction)await c.Target.BeginTransactionAsync(ct); await using var r=await c.ReadAllAsync(t,ct);var o=BuilderContext.Ordinals(r);
        while(await r.ReadAsync(ct)) await c.ExecuteAsync("INSERT INTO produto_fornecedores(produto_id,fornecedor_id,codigo_fornecedor,custo,principal,raw_json) VALUES(@p,@f,@c,@v,@pr,@r)",new Dictionary<string,object?>{{"@p",BuilderContext.DbValue(BuilderContext.Text(r,o,"PCIPMAT_ID","COMPROD_ID"))},{"@f",BuilderContext.DbValue(BuilderContext.Text(r,o,"PARENTD_ID"))},{"@c",BuilderContext.DbValue(BuilderContext.Text(r,o,"CODG_PRODUTO_FORNECEDOR"))},{"@v",BuilderContext.DbValue(BuilderContext.Text(r,o,"VALR_CUSTO"))},{"@pr",BuilderContext.DbValue(BuilderContext.Text(r,o,"INDR_PRINCIPAL"))},{"@r",BuilderContext.RowJson(r)}},tx,ct);
        await tx.CommitAsync(ct);
    }
}

public sealed class EstoqueBuilder : IBuilderModule
{
    public string Name=>"ESTOQUE";
    public async Task<ModuleResult> ExecuteAsync(BuilderContext c,CancellationToken ct)
    {
        const string t="T_RRPARTS__PCI_ITEM_DEPOSITO";var result=new ModuleResult();if(!c.HasTable(t))return result;
        await using(var tx=(SqliteTransaction)await c.Target.BeginTransactionAsync(ct))
        {await using var r=await c.ReadAllAsync(t,ct);var o=BuilderContext.Ordinals(r);while(await r.ReadAsync(ct)){result.Read++;var p=BuilderContext.Text(r,o,"PCIPMAT_ID");if(p is null){result.Review++;continue;}await c.ExecuteAsync("INSERT INTO estoque(produto_id,empresa_id,deposito_id,quantidade_fisica,quantidade_reservada,quantidade_disponivel,custo_medio,ultimo_custo,ultima_compra,ultima_venda,raw_json) VALUES(@p,@e,@d,@qf,@qr,@qd,@cm,@uc,@co,@ve,@r)",new Dictionary<string,object?>{{"@p",p},{"@e",BuilderContext.DbValue(BuilderContext.Text(r,o,"PAREMPR_ID"))},{"@d",BuilderContext.DbValue(BuilderContext.Text(r,o,"PCIDEPO_ID"))},{"@qf",BuilderContext.DbValue(BuilderContext.Text(r,o,"QTDE_ESTOQUE_FISICO","QTDE_ESTOQUE","QTDE_SALDO"))},{"@qr",BuilderContext.DbValue(BuilderContext.Text(r,o,"QTDE_RESERVADA","QTDE_RESERVADO"))},{"@qd",BuilderContext.DbValue(BuilderContext.Text(r,o,"QTDE_ESTOQUE_DISPONIVEL","QTDE_DISPONIVEL"))},{"@cm",BuilderContext.DbValue(BuilderContext.Text(r,o,"VALR_CUSTO_MEDIO","VALR_CUSTO_HISTORICO"))},{"@uc",BuilderContext.DbValue(BuilderContext.Text(r,o,"VALR_ULTIMO_CUSTO"))},{"@co",BuilderContext.DbValue(BuilderContext.Text(r,o,"DATA_ULTIMA_COMPRA"))},{"@ve",BuilderContext.DbValue(BuilderContext.Text(r,o,"DATA_ULTIMA_VENDA"))},{"@r",BuilderContext.RowJson(r)}},tx,ct);result.Written++;}await tx.CommitAsync(ct);}
        if(c.HasTable("T_RRPARTS__PCI_LOCACAO")){await using var tx=(SqliteTransaction)await c.Target.BeginTransactionAsync(ct);await using var r=await c.ReadAllAsync("T_RRPARTS__PCI_LOCACAO",ct);var o=BuilderContext.Ordinals(r);while(await r.ReadAsync(ct))await c.ExecuteAsync("INSERT INTO localizacoes(produto_id,deposito_id,rua,prateleira,coluna,nivel,posicao,raw_json) VALUES(@p,@d,@rua,@pr,@co,@ni,@po,@r)",new Dictionary<string,object?>{{"@p",BuilderContext.DbValue(BuilderContext.Text(r,o,"PCIPMAT_ID"))},{"@d",BuilderContext.DbValue(BuilderContext.Text(r,o,"PCIDEPO_ID"))},{"@rua",BuilderContext.DbValue(BuilderContext.Text(r,o,"DESC_RUA","RUA"))},{"@pr",BuilderContext.DbValue(BuilderContext.Text(r,o,"DESC_PRATELEIRA","PRATELEIRA"))},{"@co",BuilderContext.DbValue(BuilderContext.Text(r,o,"DESC_COLUNA","COLUNA"))},{"@ni",BuilderContext.DbValue(BuilderContext.Text(r,o,"DESC_NIVEL","NIVEL"))},{"@po",BuilderContext.DbValue(BuilderContext.Text(r,o,"DESC_POSICAO","POSICAO"))},{"@r",BuilderContext.RowJson(r)}},tx,ct);await tx.CommitAsync(ct);}
        return result;
    }
}

public sealed class FornecedorBuilder:IBuilderModule
{
    public string Name=>"FORNECEDORES";
    public async Task<ModuleResult> ExecuteAsync(BuilderContext c,CancellationToken ct)
    {
        const string t="T_RRPARTS__PAR_FORNECEDOR";var result=new ModuleResult();if(!c.HasTable(t))return result;
        await using var tx=(SqliteTransaction)await c.Target.BeginTransactionAsync(ct);await using var r=await c.ReadAllAsync(t,ct);var o=BuilderContext.Ordinals(r);
        while(await r.ReadAsync(ct)){result.Read++;var id=BuilderContext.Text(r,o,"PARENTD_ID","ID");if(id is null){result.Review++;continue;}await c.ExecuteAsync("INSERT OR REPLACE INTO fornecedores(id,nome,cpf_cnpj,email,telefone,raw_json) SELECT @id,nome,cpf_cnpj,email,telefone,@raw FROM clientes WHERE id=@id",new Dictionary<string,object?>{{"@id",id},{"@raw",BuilderContext.RowJson(r)}},tx,ct);result.Written++;}
        await tx.CommitAsync(ct);return result;
    }
}
