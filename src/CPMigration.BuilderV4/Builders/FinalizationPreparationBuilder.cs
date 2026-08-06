using Microsoft.Data.Sqlite;
using CPMigration.BuilderV4.Core;

namespace CPMigration.BuilderV4.Builders;

/// <summary>
/// Prepara os índices necessários para que as validações finais não façam
/// varreduras combinatórias em tabelas grandes, especialmente preços x produtos.
/// </summary>
public sealed class FinalizationPreparationBuilder : IBuilderModule
{
    public string Name => "PREPARACAO_FINAL";

    public async Task<ModuleResult> ExecuteAsync(BuilderContext context, CancellationToken ct)
    {
        Console.WriteLine("  Criando índices para validação rápida...");
        await context.LogAsync("PREPARACAO_FINAL", "Criando índices antes da validação final...");

        var statements = new[]
        {
            "CREATE INDEX IF NOT EXISTS idx_clientes_documento_pre ON clientes(cpf_cnpj)",
            "CREATE INDEX IF NOT EXISTS idx_fornecedores_id_pre ON fornecedores(id)",
            "CREATE INDEX IF NOT EXISTS idx_produtos_id_pre ON produtos(id)",
            "CREATE INDEX IF NOT EXISTS idx_produtos_comprod_pre ON produtos(comprod_id)",
            "CREATE INDEX IF NOT EXISTS idx_produtos_codigo_pre ON produtos(codigo)",
            "CREATE INDEX IF NOT EXISTS idx_estoque_produto_pre ON estoque(produto_id)",
            "CREATE INDEX IF NOT EXISTS idx_precos_produto_pre ON produto_precos(produto_id)",
            "CREATE INDEX IF NOT EXISTS idx_aplicacoes_produto_pre ON produto_aplicacoes(produto_id)",
            "CREATE INDEX IF NOT EXISTS idx_produto_fornecedores_produto_pre ON produto_fornecedores(produto_id)"
        };

        foreach (var sql in statements)
        {
            await using var command = context.Target.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 60;
            await command.ExecuteNonQueryAsync(ct);
        }

        await using (var analyze = context.Target.CreateCommand())
        {
            analyze.CommandText = "ANALYZE;";
            analyze.CommandTimeout = 60;
            await analyze.ExecuteNonQueryAsync(ct);
        }

        await context.LogAsync("PREPARACAO_FINAL", "Índices criados. Validação pode prosseguir.");
        Console.WriteLine("  Índices concluídos. Iniciando validação final...");

        return new ModuleResult
        {
            Read = statements.Length,
            Written = statements.Length,
            Review = 0
        };
    }
}
