using CPMigration.BuilderV4.Core;
using CPMigration.BuilderV4.Builders;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var root = Directory.GetCurrentDirectory();
var source = GetArg("--source") ?? Path.Combine(root, "output", "erp_intermediario.sqlite");
var output = GetArg("--output") ?? Path.Combine(root, "NovoERP-V4");

var modules = new IBuilderModule[]
{
    new ClienteBuilder(),
    new ProdutoBuilder(),
    new EstoqueBuilder(),
    new FornecedorBuilder(),
    new FinalizationPreparationBuilder()
};

var engine = new BuilderEngine(source, output, modules);
Environment.ExitCode = await engine.RunAsync();

string? GetArg(string name)
{
    var index = Array.FindIndex(args, x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;
}
