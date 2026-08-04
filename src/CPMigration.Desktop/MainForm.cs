using System.Diagnostics;
using System.Text;

namespace CPMigration.Desktop;

public sealed class MainForm : Form
{
    private readonly TextBox _input = new();
    private readonly TextBox _output = new();
    private readonly RichTextBox _log = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _status = new();
    private readonly Label _elapsed = new();
    private readonly Button _run = new();
    private readonly Button _cancel = new();
    private readonly Button _open = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private Process? _process;
    private Stopwatch? _watch;

    private static readonly Color Background = Color.FromArgb(13, 18, 28);
    private static readonly Color Surface = Color.FromArgb(23, 30, 44);
    private static readonly Color Surface2 = Color.FromArgb(32, 41, 58);
    private static readonly Color Accent = Color.FromArgb(65, 138, 255);
    private static readonly Color Success = Color.FromArgb(38, 194, 129);
    private static readonly Color TextPrimary = Color.FromArgb(239, 244, 255);
    private static readonly Color TextSecondary = Color.FromArgb(157, 170, 195);

    public MainForm()
    {
        Text = "CP Migration Studio";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 680);
        Size = new Size(1180, 780);
        BackColor = Background;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 10F);
        DoubleBuffered = true;

        Controls.Add(BuildLayout());
        Load += (_, _) => InitializePaths();
        FormClosing += (_, e) =>
        {
            if (_process is { HasExited: false })
            {
                var answer = MessageBox.Show("Existe um processamento em andamento. Deseja cancelar e sair?", "CP Migration", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer == DialogResult.No) { e.Cancel = true; return; }
                TryKillProcess();
            }
        };

        _timer.Tick += (_, _) =>
        {
            if (_watch is not null) _elapsed.Text = $"Tempo: {_watch.Elapsed:hh\\:mm\\:ss}";
        };
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 185));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new Panel { Dock = DockStyle.Fill };
        var title = new Label { Text = "CP MIGRATION STUDIO", Font = new Font("Segoe UI Semibold", 22F), AutoSize = true, ForeColor = TextPrimary, Location = new Point(0, 6) };
        var subtitle = new Label { Text = "Engenharia reversa e conversão completa do ERP antigo", Font = new Font("Segoe UI", 10.5F), AutoSize = true, ForeColor = TextSecondary, Location = new Point(3, 49) };
        header.Controls.Add(title); header.Controls.Add(subtitle);

        var configCard = Card();
        var config = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 3, RowCount = 3 };
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        config.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        config.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        config.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        AddPathRow(config, 0, "Backup extraído", _input, "Selecionar", SelectInput);
        AddPathRow(config, 1, "Pasta de saída", _output, "Selecionar", SelectOutput);
        var note = new Label { Text = "Os CSVs originais nunca são alterados. Falhas são registradas e os dados não reconhecidos ficam separados para revisão.", Dock = DockStyle.Fill, ForeColor = TextSecondary, TextAlign = ContentAlignment.MiddleLeft };
        config.Controls.Add(note, 1, 2); config.SetColumnSpan(note, 2);
        configCard.Controls.Add(config);

        var actionBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, Padding = new Padding(0, 12, 0, 8) };
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        ConfigureButton(_run, "EXECUTAR TUDO", Accent, Color.White, RunPipeline);
        ConfigureButton(_cancel, "CANCELAR", Color.FromArgb(93, 49, 58), Color.FromArgb(255, 192, 203), (_, _) => CancelPipeline());
        ConfigureButton(_open, "ABRIR MIGRATION", Surface2, TextPrimary, (_, _) => OpenMigration());
        _cancel.Enabled = false;
        actionBar.Controls.Add(_run, 0, 0); actionBar.Controls.Add(_cancel, 1, 0); actionBar.Controls.Add(_open, 2, 0);
        _elapsed.Text = "Tempo: 00:00:00"; _elapsed.Dock = DockStyle.Fill; _elapsed.TextAlign = ContentAlignment.MiddleRight; _elapsed.ForeColor = TextSecondary;
        actionBar.Controls.Add(_elapsed, 4, 0);

        var logCard = Card();
        var logLayout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 3 };
        logLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        logLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _status.Text = "Pronto para iniciar"; _status.Dock = DockStyle.Fill; _status.Font = new Font("Segoe UI Semibold", 11F); _status.ForeColor = TextPrimary;
        _progress.Dock = DockStyle.Fill; _progress.Style = ProgressBarStyle.Blocks; _progress.Minimum = 0; _progress.Maximum = 100;
        _log.Dock = DockStyle.Fill; _log.ReadOnly = true; _log.BackColor = Color.FromArgb(10, 14, 22); _log.ForeColor = Color.FromArgb(204, 216, 238); _log.BorderStyle = BorderStyle.None; _log.Font = new Font("Cascadia Mono", 9F); _log.DetectUrls = false;
        logLayout.Controls.Add(_status, 0, 0); logLayout.Controls.Add(_progress, 0, 1); logLayout.Controls.Add(_log, 0, 2);
        logCard.Controls.Add(logLayout);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(configCard, 0, 1);
        root.Controls.Add(actionBar, 0, 2);
        root.Controls.Add(logCard, 0, 3);
        return root;
    }

    private static Panel Card() => new() { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(1), Margin = new Padding(0, 4, 0, 4) };

    private void AddPathRow(TableLayoutPanel panel, int row, string label, TextBox box, string buttonText, EventHandler handler)
    {
        var caption = new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = TextSecondary };
        box.Dock = DockStyle.Fill; box.BackColor = Surface2; box.ForeColor = TextPrimary; box.BorderStyle = BorderStyle.FixedSingle; box.Margin = new Padding(0, 9, 12, 9);
        var button = new Button(); ConfigureButton(button, buttonText, Surface2, TextPrimary, handler);
        panel.Controls.Add(caption, 0, row); panel.Controls.Add(box, 1, row); panel.Controls.Add(button, 2, row);
    }

    private static void ConfigureButton(Button button, string text, Color background, Color foreground, EventHandler handler)
    {
        button.Text = text; button.Dock = DockStyle.Fill; button.Margin = new Padding(0, 4, 10, 4);
        button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 0; button.BackColor = background; button.ForeColor = foreground;
        button.Font = new Font("Segoe UI Semibold", 9.5F); button.Cursor = Cursors.Hand; button.Click += handler;
    }

    private void InitializePaths()
    {
        var root = FindRepositoryRoot(AppContext.BaseDirectory) ?? Directory.GetCurrentDirectory();
        _input.Text = Path.Combine(root, "input");
        _output.Text = Path.Combine(root, "output");
    }

    private void SelectInput(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog { Description = "Selecione a pasta que contém os CSVs extraídos", UseDescriptionForTitle = true, SelectedPath = _input.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK) _input.Text = dialog.SelectedPath;
    }

    private void SelectOutput(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog { Description = "Selecione a pasta onde os resultados serão gerados", UseDescriptionForTitle = true, SelectedPath = _output.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK) _output.Text = dialog.SelectedPath;
    }

    private async void RunPipeline(object? sender, EventArgs e)
    {
        if (_process is { HasExited: false }) return;
        var input = _input.Text.Trim(); var output = _output.Text.Trim();
        if (!Directory.Exists(input)) { MessageBox.Show("A pasta do backup não existe.", "CP Migration", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (!Directory.EnumerateFiles(input, "*.csv", SearchOption.AllDirectories).Any()) { MessageBox.Show("Nenhum CSV foi encontrado nessa pasta.", "CP Migration", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        Directory.CreateDirectory(output);

        _log.Clear(); _progress.Value = 0; _progress.Style = ProgressBarStyle.Marquee; _status.Text = "Preparando processamento...";
        _run.Enabled = false; _cancel.Enabled = true; _watch = Stopwatch.StartNew(); _timer.Start();

        try
        {
            var startInfo = BuildEngineStartInfo(input, output);
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, args) => { if (args.Data is not null) BeginInvoke(() => AppendLog(args.Data)); };
            _process.ErrorDataReceived += (_, args) => { if (args.Data is not null) BeginInvoke(() => AppendLog("[ERRO] " + args.Data, true)); };
            _process.Start(); _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
            await _process.WaitForExitAsync();

            _progress.Style = ProgressBarStyle.Blocks; _progress.Value = 100;
            if (_process.ExitCode == 0)
            {
                _status.Text = "Processamento concluído com sucesso"; _status.ForeColor = Success;
                AppendLog("\nPROCESSAMENTO CONCLUÍDO. Resultado disponível na pasta Migration.");
                MessageBox.Show("Processamento concluído. Todos os resultados estão organizados em output\\Migration.", "CP Migration", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                _status.Text = "Processamento interrompido — consulte o log"; _status.ForeColor = Color.FromArgb(255, 120, 120);
                MessageBox.Show("O processamento terminou com erro. Consulte o log exibido e output\\pipeline.log.", "CP Migration", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            AppendLog(ex.ToString(), true); _status.Text = "Falha ao iniciar o processamento";
            MessageBox.Show(ex.Message, "CP Migration", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _timer.Stop(); _watch?.Stop(); _run.Enabled = true; _cancel.Enabled = false; _process?.Dispose(); _process = null;
        }
    }

    private ProcessStartInfo BuildEngineStartInfo(string input, string output)
    {
        var appDirectory = AppContext.BaseDirectory;
        var engineExe = Path.Combine(appDirectory, "engine", "CPMigration.Cli.exe");
        if (File.Exists(engineExe))
            return CreateStartInfo(engineExe, $"--input \"{input}\" --output \"{output}\"");

        var root = FindRepositoryRoot(appDirectory) ?? throw new DirectoryNotFoundException("Não foi possível localizar a raiz do projeto nem o mecanismo publicado.");
        var project = Path.Combine(root, "src", "CPMigration.Cli", "CPMigration.Cli.csproj");
        return CreateStartInfo("dotnet", $"run --project \"{project}\" -c Release -- --input \"{input}\" --output \"{output}\"");
    }

    private static ProcessStartInfo CreateStartInfo(string file, string arguments) => new()
    {
        FileName = file, Arguments = arguments, UseShellExecute = false, RedirectStandardOutput = true,
        RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
    };

    private void AppendLog(string line, bool error = false)
    {
        _log.SelectionStart = _log.TextLength; _log.SelectionColor = error ? Color.FromArgb(255, 128, 128) : Color.FromArgb(204, 216, 238);
        _log.AppendText(line + Environment.NewLine); _log.ScrollToCaret();
        if (line.Contains("STEP_1")) _status.Text = "1/5 — Importando CSVs para SQLite";
        else if (line.Contains("STEP_2")) _status.Text = "2/5 — Descobrindo tabelas e relacionamentos";
        else if (line.Contains("STEP_3")) _status.Text = "3/5 — Exportando todas as tabelas";
        else if (line.Contains("STEP_4")) _status.Text = "4/5 — Consolidando módulos do ERP";
        else if (line.Contains("STEP_5")) _status.Text = "5/5 — Validando integridade dos dados";
    }

    private void CancelPipeline()
    {
        if (_process is not { HasExited: false }) return;
        if (MessageBox.Show("Deseja cancelar o processamento atual?", "CP Migration", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
        {
            TryKillProcess(); _status.Text = "Cancelando processamento...";
        }
    }

    private void TryKillProcess()
    {
        try { _process?.Kill(entireProcessTree: true); } catch { }
    }

    private void OpenMigration()
    {
        var path = Path.Combine(_output.Text.Trim(), "Migration");
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private static string? FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EXECUTAR_TUDO.cmd")) || Directory.Exists(Path.Combine(directory.FullName, ".git"))) return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }
}
