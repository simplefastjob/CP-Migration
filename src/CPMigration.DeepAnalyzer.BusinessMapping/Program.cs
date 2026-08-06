using System.Text;
using System.Text.Json;

Console.OutputEncoding = Encoding.UTF8;
var root = Directory.GetCurrentDirectory();
var structurePath = Arg("--structure") ?? Path.Combine(root, "AnaliseCompleta", "EstruturaCompleta.json");
var relationshipsPath = Arg("--relationships") ?? Path.Combine(root, "AnaliseCompleta", "RelacionamentosDescobertos.json");
var output = Arg("--output") ?? Path.Combine(root, "AnaliseCompleta");
Directory.CreateDirectory(output);

if (!File.Exists(structurePath)) throw new FileNotFoundException("EstruturaCompleta.json não encontrado.", structurePath);
if (!File.Exists(relationshipsPath)) throw new FileNotFoundException("RelacionamentosDescobertos.json não encontrado.", relationshipsPath);

var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var structure = JsonSerializer.Deserialize<DatabaseStructure>(await File.ReadAllTextAsync(structurePath), jsonOptions)
    ?? throw new InvalidOperationException("Falha ao ler EstruturaCompleta.json.");
var relationData = JsonSerializer.Deserialize<RelationshipAnalysis>(await File.ReadAllTextAsync(relationshipsPath), jsonOptions)
    ?? throw new InvalidOperationException("Falha ao ler RelacionamentosDescobertos.json.");

var tables = structure.Tables.Where(t => t.RowCount > 0).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
var relationships = relationData.Relationships.Where(r => r.Confidence >= .70 && r.Coverage >= .70).ToList();

var modules = BuildModules();
var result = new BusinessMapResult
{
    GeneratedAt = DateTimeOffset.Now,
    TablesWithData = tables.Count,
    RelationshipsUsed = relationships.Count,
    Modules = modules
};

await File.WriteAllTextAsync(Path.Combine(output, "MAPEAMENTO_FUNCIONAL.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
await File.WriteAllTextAsync(Path.Combine(output, "MANUAL_ERP_ANTIGO.md"), BuildManual(result), Encoding.UTF8);
await File.WriteAllTextAsync(Path.Combine(output, "PLANO_BUILDER_V2.md"), BuildBuilderPlan(result), Encoding.UTF8);
await File.WriteAllTextAsync(Path.Combine(output, "FLUXOS_NEGOCIO.md"), BuildFlows(result), Encoding.UTF8);

Console.WriteLine("Mapeamento funcional concluído.");
Console.WriteLine($"Módulos: {result.Modules.Count:N0}");
Console.WriteLine($"Relacionamentos utilizados: {result.RelationshipsUsed:N0}");

List<BusinessModule> BuildModules()
{
    var definitions = new[]
    {
        new ModuleDefinition("CLIENTES_FORNECEDORES", ["PAR_ENT", "PAR_PESSOA", "CLIENTE", "FORNECEDOR", "CONTATO", "ENDERECO", "TELEFONE", "EMAIL"], ["PARENTD_ID", "PAREMPR_ID"]),
        new ModuleDefinition("PRODUTOS", ["COM_PRODUTO", "PCI_ITEM", "PCI_FORNECEDOR_PRODUTO", "APLICACAO", "EQUIVAL", "PRECO_PRODUTO", "FOTO_PRODUTO"], ["PCIPMAT_ID", "COMPROD_ID"]),
        new ModuleDefinition("ESTOQUE_LOCALIZACAO", ["ITEM_DEPOSITO", "ITEM_EMPRESA", "LOCACAO", "DEPOSITO", "INVENTARIO", "RESUMO_MENSAL", "LOTE", "ALOCACAO"], ["PCIPMAT_ID", "PCIDEPO_ID", "PCIIEMP_ID"]),
        new ModuleDefinition("VENDAS_COMERCIAL", ["MKG_NEGOCIACAO", "MKG_PECA_NEGOCIACAO", "PCI_REQUISICAO", "VENDA", "PEDIDO", "ORCAMENTO"], ["MKGNEGO_ID", "MKGOPOR_ID"]),
        new ModuleDefinition("COMPRAS", ["COMPRA", "COTACAO", "PEDIDO_COMPRA", "FORNECEDOR_PRODUTO", "IMPORTA_NOTA", "ITEM_NOTA_FISCAL"], ["PCIPMAT_ID", "ESCDOCF_ID"]),
        new ModuleDefinition("FINANCEIRO", ["FIN_TITULO", "FIN_EMISSAO_BOLETO", "FIN_CAIXA", "FIN_BAIXA", "PAGAMENTO", "RECEBER", "PAGAR"], ["FINFATU_ID", "ESCDOCF_ID", "CODG_BARRAS"]),
        new ModuleDefinition("FISCAL", ["ESC_DOCUMENTO_FISCAL", "ESC_ITEM_ESCRITA_FISCAL", "ESC_LOG_NFE", "DOCUMENTO_FISCAL_XML", "NFE", "NFCE", "CTE"], ["ESCDOCF_ID", "ESCITEF_ID", "COMPROD_ID"]),
        new ModuleDefinition("USUARIOS_PERMISSOES", ["PAR_USUARIO", "USUARIO", "PERMISSAO", "ACESSO", "GRUPO"], ["PARUSUA_ID"]),
        new ModuleDefinition("GARANTIAS_DEVOLUCOES", ["GARANTIA", "DEVOLU", "TROCA", "RECLAMACAO"], ["PCIPMAT_ID", "MKGNEGO_ID", "ESCDOCF_ID"])
    };

    var modules = new List<BusinessModule>();
    foreach (var definition in definitions)
    {
        var candidates = tables.Values
            .Where(t => definition.TableTerms.Any(term => t.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Select(t => ScoreTable(t, definition))
            .OrderByDescending(x => x.Score)
            .ToList();

        if (candidates.Count == 0) continue;
        var principal = candidates.First();
        var selectedNames = candidates.Take(30).Select(x => x.Table.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var links = relationships
            .Where(r => selectedNames.Contains(r.ChildTable) || selectedNames.Contains(r.ParentTable))
            .OrderByDescending(r => r.Confidence)
            .ThenByDescending(r => r.Coverage)
            .Take(150)
            .ToList();

        var roles = candidates.Take(30).Select(c => new FunctionalTable
        {
            Name = c.Table.Name,
            Rows = c.Table.RowCount,
            Role = InferRole(c.Table, principal.Table.Name),
            Score = c.Score,
            KeyColumns = c.Table.Columns.Where(col => definition.KeyTerms.Any(k => col.Name.Contains(k, StringComparison.OrdinalIgnoreCase))).Select(col => col.Name).Distinct().ToList(),
            ImportantFields = c.Table.Columns
                .Where(col => col.NonNullCount > 0 && col.SuggestedMeaning != "DESCONHECIDO")
                .OrderByDescending(col => col.MeaningConfidence)
                .Take(20)
                .Select(col => new FunctionalField(col.Name, col.SuggestedMeaning, col.MeaningConfidence, col.NonNullCount, col.DistinctCount, col.Examples.Take(3).ToArray()))
                .ToList()
        }).ToList();

        modules.Add(new BusinessModule
        {
            Name = definition.Name,
            PrincipalTable = principal.Table.Name,
            Confidence = Math.Clamp(principal.Score / 220d, 0, 1),
            Tables = roles,
            Relationships = links,
            RecommendedBuildOrder = BuildOrder(roles, links)
        });
    }
    return modules;
}

ScoredTable ScoreTable(TableInfo table, ModuleDefinition definition)
{
    var nameHits = definition.TableTerms.Count(term => table.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
    var keyHits = table.Columns.Count(c => definition.KeyTerms.Any(k => c.Name.Contains(k, StringComparison.OrdinalIgnoreCase)));
    var outgoing = relationships.Count(r => r.ChildTable.Equals(table.Name, StringComparison.OrdinalIgnoreCase));
    var incoming = relationships.Count(r => r.ParentTable.Equals(table.Name, StringComparison.OrdinalIgnoreCase));
    var uniqueCandidates = table.Columns.Count(c => c.NonNullCount > 0 && c.DistinctCount >= c.NonNullCount * .95);
    var score = nameHits * 55d + keyHits * 18d + incoming * 5d + outgoing * 2d + uniqueCandidates * 4d + Math.Log10(Math.Max(1, table.RowCount)) * 8d;
    return new ScoredTable(table, score);
}

string InferRole(TableInfo table, string principal)
{
    if (table.Name.Equals(principal, StringComparison.OrdinalIgnoreCase)) return "PRINCIPAL";
    var n = table.Name.ToUpperInvariant();
    if (n.Contains("HIST") || n.Contains("LOG") || n.Contains("RESUMO_MENSAL")) return "HISTORICO";
    if (n.Contains("ITEM") || n.Contains("DETALHE") || n.Contains("PARCELA")) return "DETALHE";
    if (n.Contains("PRECO") || n.Contains("CUSTO")) return "PRECIFICACAO";
    if (n.Contains("LOCACAO") || n.Contains("DEPOSITO") || n.Contains("ESTOQUE")) return "ESTOQUE_LOCALIZACAO";
    if (n.Contains("CODIGO") || n.Contains("APLICACAO") || n.Contains("EQUIVAL") || n.Contains("FOTO")) return "COMPLEMENTAR";
    if (n.Contains("COMPL") || n.Contains("CONFIG") || n.Contains("PARAMETRO")) return "AUXILIAR";
    return "RELACIONADA";
}

List<string> BuildOrder(List<FunctionalTable> moduleTables, List<RelationshipInfo> links)
{
    return moduleTables
        .OrderBy(t => t.Role == "PRINCIPAL" ? 0 : t.Role == "AUXILIAR" ? 1 : t.Role == "COMPLEMENTAR" ? 2 : t.Role == "DETALHE" ? 3 : t.Role == "HISTORICO" ? 5 : 4)
        .ThenByDescending(t => links.Count(r => r.ParentTable == t.Name))
        .Select(t => t.Name)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

string BuildManual(BusinessMapResult map)
{
    var sb = new StringBuilder("# Manual funcional do ERP antigo\n\n");
    sb.AppendLine($"- Tabelas com dados analisadas: **{map.TablesWithData:N0}**");
    sb.AppendLine($"- Relacionamentos confiáveis utilizados: **{map.RelationshipsUsed:N0}**\n");
    foreach (var module in map.Modules)
    {
        sb.AppendLine($"## {module.Name}\n");
        sb.AppendLine($"- Tabela principal sugerida: `{module.PrincipalTable}`");
        sb.AppendLine($"- Confiança: **{module.Confidence:P0}**\n");
        sb.AppendLine("| Tabela | Papel | Registros | Chaves principais | Score |");
        sb.AppendLine("|---|---|---:|---|---:|");
        foreach (var table in module.Tables.Take(25))
            sb.AppendLine($"| `{table.Name}` | {table.Role} | {table.Rows:N0} | {string.Join(", ", table.KeyColumns.Select(x => $"`{x}`"))} | {table.Score:N1} |");
        sb.AppendLine("\n### Ordem sugerida de montagem\n");
        for (var i = 0; i < module.RecommendedBuildOrder.Count; i++) sb.AppendLine($"{i + 1}. `{module.RecommendedBuildOrder[i]}`");
        sb.AppendLine("\n### Relacionamentos centrais\n");
        foreach (var r in module.Relationships.Take(25))
            sb.AppendLine($"- `{r.ChildTable}.{r.ChildColumn}` → `{r.ParentTable}.{r.ParentColumn}` — confiança {r.Confidence:P0}, cobertura {r.Coverage:P0}");
        sb.AppendLine();
    }
    return sb.ToString();
}

string BuildBuilderPlan(BusinessMapResult map)
{
    var sb = new StringBuilder("# Plano de implementação do Builder V2\n\n");
    foreach (var module in map.Modules)
    {
        sb.AppendLine($"## {module.Name}\n");
        sb.AppendLine($"1. Ler `{module.PrincipalTable}` como tabela principal.");
        sb.AppendLine("2. Criar dicionário pela chave de negócio identificada.");
        sb.AppendLine("3. Anexar tabelas auxiliares e complementares.");
        sb.AppendLine("4. Anexar detalhes transacionais sem duplicar o cadastro principal.");
        sb.AppendLine("5. Preservar histórico em tabelas separadas.");
        sb.AppendLine("6. Registrar órfãos e colisões em relatório de revisão.\n");
        sb.AppendLine("Tabelas prioritárias:");
        foreach (var table in module.RecommendedBuildOrder.Take(15)) sb.AppendLine($"- `{table}`");
        sb.AppendLine();
    }
    return sb.ToString();
}

string BuildFlows(BusinessMapResult map)
{
    var sb = new StringBuilder("# Fluxos de negócio inferidos\n\n");
    var edges = map.Modules.SelectMany(m => m.Relationships.Select(r => (Module: m.Name, Relation: r)))
        .OrderByDescending(x => x.Relation.Confidence).ThenByDescending(x => x.Relation.Coverage).Take(300);
    foreach (var edge in edges)
        sb.AppendLine($"- **{edge.Module}**: `{edge.Relation.ChildTable}` usa `{edge.Relation.ChildColumn}` para chegar em `{edge.Relation.ParentTable}.{edge.Relation.ParentColumn}`.");
    return sb.ToString();
}

string? Arg(string name)
{
    var i = Array.FindIndex(args, x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : null;
}

sealed class DatabaseStructure { public List<TableInfo> Tables { get; set; } = []; }
sealed class TableInfo { public string Name { get; set; } = ""; public long RowCount { get; set; } public string SuggestedDomain { get; set; } = ""; public double DomainConfidence { get; set; } public List<ColumnInfo> Columns { get; set; } = []; }
sealed class ColumnInfo { public string Name { get; set; } = ""; public long NonNullCount { get; set; } public long DistinctCount { get; set; } public string SuggestedMeaning { get; set; } = "DESCONHECIDO"; public double MeaningConfidence { get; set; } public List<string> Examples { get; set; } = []; }
sealed class RelationshipAnalysis { public List<RelationshipInfo> Relationships { get; set; } = []; }
sealed class RelationshipInfo { public string ChildTable { get; set; } = ""; public string ChildColumn { get; set; } = ""; public string ParentTable { get; set; } = ""; public string ParentColumn { get; set; } = ""; public double Coverage { get; set; } public double Confidence { get; set; } }
sealed record ModuleDefinition(string Name, string[] TableTerms, string[] KeyTerms);
sealed record ScoredTable(TableInfo Table, double Score);
sealed record FunctionalField(string Column, string Meaning, double Confidence, long NonNull, long Distinct, string[] Examples);
sealed class FunctionalTable { public string Name { get; set; } = ""; public long Rows { get; set; } public string Role { get; set; } = ""; public double Score { get; set; } public List<string> KeyColumns { get; set; } = []; public List<FunctionalField> ImportantFields { get; set; } = []; }
sealed class BusinessModule { public string Name { get; set; } = ""; public string PrincipalTable { get; set; } = ""; public double Confidence { get; set; } public List<FunctionalTable> Tables { get; set; } = []; public List<RelationshipInfo> Relationships { get; set; } = []; public List<string> RecommendedBuildOrder { get; set; } = []; }
sealed class BusinessMapResult { public DateTimeOffset GeneratedAt { get; set; } public int TablesWithData { get; set; } public int RelationshipsUsed { get; set; } public List<BusinessModule> Modules { get; set; } = []; }
