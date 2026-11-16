using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using HeatTransferSim.Data;
using HeatTransferSim.Models;
using HeatTransferSim.Views;

namespace HeatTransferSim.Controllers;

/// <summary>
/// Simulador de transferência de calor entre blocos (cubos) em uma grade Linhas × Colunas.
/// Esta classe é dividida em arquivos parciais (tudo na pasta Controllers):
///   Form1.cs             → campos, ciclo de vida, edição da grade (tamanho, materiais, temperaturas)
///   Form1.Gui.cs         → montagem da interface (painéis, botões, dicas)
///   Form1.Simulation.cs  → loop assíncrono: iniciar/pausar/passo/zerar
///   Form1.Database.cs    → registro das simulações no MySQL
///   Form1.Info.cs        → textos do painel "Informações" e da barra de status
/// </summary>
/// <remarks>
/// Física (detalhes em Models/HeatGrid.cs e em Anotacoes_Formulas.txt): condução de Fourier entre
/// blocos vizinhos (8 vizinhos; diagonais com peso 0.25). Entre materiais diferentes a condutância
/// vem das resistências em série (média harmônica das condutividades), então uma fila de isolante
/// realmente segura o calor. Calor sensível Q = m·c·ΔT. NÃO há fonte de calor contínua: a energia
/// total é conservada, a temperatura converge para o equilíbrio e a simulação pausa sozinha.
///
/// Materiais: um "material geral" para a grade toda (combo "Material geral") E, se quiser, um material
/// PRÓPRIO para qualquer bloco (selecione os blocos na grade, escolha o material e clique em "Aplicar").
///
/// Cálculo paralelo e assíncrono: as células de cada passo são calculadas em paralelo no thread pool
/// (HeatGrid.StepManyAsync); a interface nunca trava. Por isso os controles que mudariam a grade
/// ficam desabilitados enquanto a simulação roda (<see cref="IsBusy"/>), e um cálculo nunca se
/// sobrepõe a outro (<see cref="_isSteppingAsync"/>).
///
/// Regra: temperaturas/materiais iniciais só fazem sentido em t = 0. Se você editar a configuração
/// DEPOIS que a simulação já calculou passos, ela recomeça do zero (nova simulação e nova linha no banco).
/// </remarks>
public partial class Form1 : Form
{
    private const string RestartNote = " | Simulação reiniciada do zero com a nova configuração.";

    private HeatGrid? _grid;
    private readonly TemperatureGridControl _gridCtrl;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolTip _tip = new();

    // ===== Estado da simulação/banco =====
    private bool _isRunning;                  // loop automático ligado ("Iniciar" → "Pausa")
    private bool _isSingleStepping;           // botão "Passo" em andamento
    private bool _isSteppingAsync;            // um cálculo assíncrono em andamento (nunca dois ao mesmo tempo)
    private Task? _dbInitTask;                // verificação/criação do esquema do banco (roda ao abrir a janela)
    private bool _dbAvailable;                // banco acessível? vira false se uma chamada falhar
    private int? _currentSimulationId;        // Id da simulação atual no banco (null = ainda não registrada)
    private bool _equilibriumLogged;          // o equilíbrio desta simulação já foi gravado?
    private int _ticksSinceLastDbUpdate;      // throttle das atualizações de progresso no banco
    private double _initialSpread;            // (T máx − T mín) no instante zero, para o % de convergência

    private bool IsBusy => _isRunning || _isSingleStepping;

    /// <summary>Passos de tempo calculados por quadro de animação (do slider logarítmico: 1 a 1000).</summary>
    private int StepsPerFrame => Math.Max(1, (int)Math.Round(Math.Pow(10, sldSpeed.Value / 10.0)));

    /// <summary>Mostra a velocidade em passos/quadro e quanto tempo SIMULADO passa por segundo real (aprox.).</summary>
    private void UpdateSpeedLabel()
    {
        double dt = _grid?.SafeTimeStep ?? 0.0;
        string rate = dt > 0
            ? $"\r\n≈ {FormatRate(StepsPerFrame * dt * 1000.0 / _timer.Interval)} de tempo simulado por segundo"
            : "";
        lblSpeed.Text = $"Velocidade: {StepsPerFrame} passo(s) por quadro{rate}";
    }

    // ===== Controles: grade e material geral =====
    private readonly NumericUpDown numRows = MkNum(1, 40, 1, 5);
    private readonly NumericUpDown numCols = MkNum(1, 40, 1, 5);
    private readonly NumericUpDown numSide = MkNum(0.001, 100, 0.01, 0.05, 3);
    private readonly ComboBox cboMaterial = new() { DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name", Width = 170 };
    private readonly Button btnAllMaterial = new() { Text = "Todos os blocos = este material", AutoSize = true };
    private readonly Button btnCreate = new() { Text = "Criar grade 5×5", AutoSize = true };

    // ===== Controles: blocos selecionados =====
    private readonly Label lblSelection = new() { Text = "", AutoSize = true, MaximumSize = new Size(380, 0) };
    private readonly NumericUpDown numBlockTemp = MkNum(-273, 1500, 10, 300, 1);
    private readonly Button btnApplyTemp = new() { Text = "Aplicar", AutoSize = true };
    private readonly Button btnClearTemp = new() { Text = "Remover", AutoSize = true };
    private readonly ComboBox cboBlockMaterial = new() { DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name", Width = 170 };
    private readonly Button btnApplyBlockMaterial = new() { Text = "Aplicar", AutoSize = true };
    private readonly Button btnResetBlockMaterial = new() { Text = "Padrão", AutoSize = true };
    private readonly Button btnSelectAll = new() { Text = "Selecionar tudo", AutoSize = true };
    private readonly Button btnClearSelection = new() { Text = "Limpar seleção", AutoSize = true };
    private readonly Button btnClearAllTemps = new() { Text = "Limpar temperaturas", AutoSize = true };

    // ===== Controles: simulação =====
    private readonly Button btnStep = new() { Text = "Passo (1 dt)", AutoSize = true };
    private readonly Button btnReset = new() { Text = "Zerar temperaturas", AutoSize = true };
    private readonly Button btnPlay = new() { Text = "Iniciar", AutoSize = true };
    // Velocidade em escala logarítmica: posição 0..30 → 1..1000 passos por quadro (posição 5 ≈ 3 passos).
    private readonly TrackBar sldSpeed = new() { Minimum = 0, Maximum = 30, Value = 5, TickFrequency = 5, Width = 220 };
    private readonly Label lblSpeed = new() { Text = "Velocidade:", AutoSize = true, MaximumSize = new Size(300, 0) };
    private readonly Label lblTime = new() { Text = "Tempo: 0.00 s", AutoSize = true, MaximumSize = new Size(300, 0) };
    private readonly Label lblDt = new() { Text = "dt: —", AutoSize = true };

    // ===== Controles: informações úteis =====
    private readonly Label lblMaterialInfo = new() { Text = "Material geral: —", AutoSize = true, MaximumSize = new Size(300, 0) };
    private readonly Label lblComposition = new() { Text = "Composição: —", AutoSize = true, MaximumSize = new Size(300, 0) };
    private readonly Label lblBlocksInfo = new() { Text = "Blocos: —", AutoSize = true, MaximumSize = new Size(300, 0) };
    private readonly Label lblEquilibriumInfo = new() { Text = "T° final prevista: —", AutoSize = true, MaximumSize = new Size(300, 0) };
    private readonly Label lblConvergence = new() { Text = "Convergência: —", AutoSize = true };
    private readonly Label lblDbStatus = new() { Text = "Banco: verificando conexão...", AutoSize = true, MaximumSize = new Size(300, 0) };

    private readonly Label lblStatus = new() { Text = "Defina linhas e colunas e clique em \"Criar grade\".", AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) };

    public Form1()
    {
        InitializeComponent();
        _timer = new System.Windows.Forms.Timer { Interval = 33 };
        _timer.Tick += Timer_Tick;
        _gridCtrl = new TemperatureGridControl { Dock = DockStyle.Fill };
        _gridCtrl.SelectionChanged += OnSelectionChanged;
        BuildGui();
        PopulateMaterials();
        UpdateSelectionUI();
        UpdateInfo(true);
    }

    /// <summary>
    /// Depois que a janela aparece, verifica a conexão com o MySQL e cria as tabelas (se faltarem)
    /// em segundo plano — a janela abre na hora, mesmo que o banco esteja desligado ou lento.
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _dbInitTask = InitializeDatabaseAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _timer.Stop();
        _isRunning = false;
        base.OnFormClosing(e);
    }

    private void PopulateMaterials()
    {
        cboMaterial.Items.Clear();
        cboBlockMaterial.Items.Clear();
        foreach (var m in Materials.All)
        {
            cboMaterial.Items.Add(m);
            cboBlockMaterial.Items.Add(m);
        }
        cboMaterial.SelectedIndex = 1; // Cobre: condutor comum
        cboBlockMaterial.SelectedIndex = Math.Max(0, Array.FindIndex(Materials.All, m => m.Name.StartsWith("Madeira")));
    }

    // =====================================================================
    //  Grade / configuração geral
    // =====================================================================

    private void CreateGrid()
    {
        if (_isSteppingAsync || IsBusy) return;

        int rows = (int)numRows.Value;
        int cols = (int)numCols.Value;
        double side = Decimal.ToDouble(numSide.Value);
        var material = cboMaterial.SelectedItem as Material ?? Materials.All[0];

        // Mantém as temperaturas iniciais e os materiais próprios já pintados na grade anterior (o que
        // não couber no novo tamanho é descartado). Na primeiríssima vez começa com um bloco quente
        // em (0, 0), só pra já ter algo acontecendo quando você clicar em "Iniciar".
        IReadOnlyDictionary<(int row, int col), double> initial = _grid?.InitialTemperatures
            ?? new Dictionary<(int row, int col), double> { [(0, 0)] = 300.0 };
        IReadOnlyDictionary<(int row, int col), Material>? ownMaterials = _grid?.MaterialOverrides;

        _grid = new HeatGrid(rows, cols, side, material, initial, ownMaterials);
        _gridCtrl.Grid = _grid;          // também limpa a seleção
        _currentSimulationId = null;     // nova simulação: só é registrada no banco no primeiro passo calculado
        _equilibriumLogged = false;
        UpdateSelectionUI();
        UpdateInfo(true);
        lblStatus.Text = $"Grade {rows}×{cols} criada (cubos de lado {side} m). Selecione blocos para dar temperatura ou material a eles.";
    }

    /// <summary>Mantém o texto do botão de criar coerente com o tamanho digitado.</summary>
    private void UpdateCreateButtonText() =>
        btnCreate.Text = $"Criar grade {(int)numRows.Value}×{(int)numCols.Value}";

    /// <summary>O combo "Material geral" mudou: vale para os blocos que NÃO têm material próprio.</summary>
    private void DefaultMaterialChanged()
    {
        var mat = cboMaterial.SelectedItem as Material ?? Materials.All[0];
        var grid = _grid;
        if (grid == null)
        {
            UpdateMaterialInfoLabel(mat);
            return;
        }
        if (_isSteppingAsync || ReferenceEquals(mat, grid.DefaultMaterial)) return;

        bool restarted = RestartIfAlreadyStarted();
        grid.SetDefaultMaterial(mat);
        AfterConfigChanged(restarted);
    }

    /// <summary>Botão "Todos os blocos = este material": apaga os materiais individuais e usa um só na grade toda.</summary>
    private void ApplyDefaultMaterialToAll()
    {
        var grid = _grid;
        if (grid == null || _isSteppingAsync || IsBusy) return;
        var mat = cboMaterial.SelectedItem as Material ?? Materials.All[0];

        bool restarted = RestartIfAlreadyStarted();
        grid.SetAllMaterials(mat);
        AfterConfigChanged(restarted);
    }

    private void SideChanged()
    {
        var grid = _grid;
        if (grid == null || _isSteppingAsync) return;

        bool restarted = RestartIfAlreadyStarted();
        try { grid.SetSide(Decimal.ToDouble(numSide.Value)); }
        catch (ArgumentOutOfRangeException) { }
        AfterConfigChanged(restarted);
    }

    /// <summary>
    /// Se a simulação já calculou passos, recomeça do zero (todos os blocos voltam às temperaturas
    /// iniciais e começa uma nova simulação). Chamado antes de qualquer edição de configuração.
    /// </summary>
    /// <returns>true se reiniciou.</returns>
    private bool RestartIfAlreadyStarted()
    {
        var grid = _grid;
        if (grid == null || grid.StepsComputed == 0) return false;

        grid.ResetTemperatures();
        _currentSimulationId = null;
        _equilibriumLogged = false;
        return true;
    }

    private void AfterConfigChanged(bool restarted)
    {
        _gridCtrl.RefreshGrid();
        UpdateSelectionUI();
        UpdateInfo(true);
        if (restarted) lblStatus.Text += RestartNote;
    }

    // =====================================================================
    //  Blocos selecionados: temperatura e material
    // =====================================================================

    private List<(int row, int col)> SelectedCells() => _gridCtrl.SelectedCells.ToList();

    private void OnSelectionChanged()
    {
        var grid = _grid;
        if (grid != null && _gridCtrl.SelectedCells.Count == 1)
        {
            // Com um bloco só, já mostra os valores dele nos campos de edição.
            var (r, c) = _gridCtrl.SelectedCells.First();
            Body b = grid.Bodies[r, c];
            numBlockTemp.Value = (decimal)Math.Clamp(Math.Round(b.Temperature, 1), -273, 1500);
            int idx = cboBlockMaterial.Items.IndexOf(b.Material);
            if (idx >= 0) cboBlockMaterial.SelectedIndex = idx;
        }
        UpdateSelectionUI();
    }

    private void ApplyTemperatureToSelection()
    {
        var grid = _grid;
        var cells = SelectedCells();
        if (grid == null || cells.Count == 0 || _isSteppingAsync || IsBusy) return;

        bool restarted = RestartIfAlreadyStarted();
        grid.SetInitialTemperatures(cells, Decimal.ToDouble(numBlockTemp.Value));
        AfterConfigChanged(restarted);
    }

    private void ClearTemperatureOfSelection()
    {
        var grid = _grid;
        var cells = SelectedCells();
        if (grid == null || cells.Count == 0 || _isSteppingAsync || IsBusy) return;
        if (!cells.Any(c => grid.InitialTemperatures.ContainsKey(c))) return;

        bool restarted = RestartIfAlreadyStarted();
        grid.ClearInitialTemperatures(cells);
        AfterConfigChanged(restarted);
    }

    private void ClearAllTemperatures()
    {
        var grid = _grid;
        if (grid == null || _isSteppingAsync || IsBusy) return;

        bool restarted = RestartIfAlreadyStarted();
        grid.ClearAllInitialTemperatures();
        AfterConfigChanged(restarted);
    }

    private void ApplyMaterialToSelection()
    {
        var grid = _grid;
        var cells = SelectedCells();
        if (grid == null || cells.Count == 0 || _isSteppingAsync || IsBusy) return;
        var mat = cboBlockMaterial.SelectedItem as Material;
        if (mat == null) return;

        bool restarted = RestartIfAlreadyStarted();
        grid.SetBlockMaterials(cells, mat);
        AfterConfigChanged(restarted);
    }

    private void ResetMaterialOfSelection()
    {
        var grid = _grid;
        var cells = SelectedCells();
        if (grid == null || cells.Count == 0 || _isSteppingAsync || IsBusy) return;
        if (!cells.Any(c => grid.MaterialOverrides.ContainsKey(c))) return;

        bool restarted = RestartIfAlreadyStarted();
        grid.ClearBlockMaterials(cells);
        AfterConfigChanged(restarted);
    }

    // =====================================================================
    //  Habilitar / desabilitar controles
    // =====================================================================

    /// <summary>
    /// Enquanto a simulação roda (ou um passo está sendo calculado) os controles que alterariam a
    /// grade ficam desabilitados — evita mexer nos dados enquanto outra thread calcula.
    /// Selecionar blocos para ver os valores continua liberado.
    /// </summary>
    private void RefreshEnabledState()
    {
        bool free = !IsBusy;
        numRows.Enabled = free;
        numCols.Enabled = free;
        numSide.Enabled = free;
        cboMaterial.Enabled = free;
        btnCreate.Enabled = free;
        btnStep.Enabled = free;
        UpdateSelectionUI();
    }

    private void UpdateSelectionUI()
    {
        var grid = _grid;
        var cells = _gridCtrl.SelectedCells;
        int n = cells.Count;
        bool canEdit = grid != null && !IsBusy;

        numBlockTemp.Enabled = canEdit && n > 0;
        btnApplyTemp.Enabled = canEdit && n > 0;
        btnClearTemp.Enabled = canEdit && cells.Any(c => grid!.InitialTemperatures.ContainsKey(c));
        cboBlockMaterial.Enabled = canEdit && n > 0;
        btnApplyBlockMaterial.Enabled = canEdit && n > 0;
        btnResetBlockMaterial.Enabled = canEdit && cells.Any(c => grid!.MaterialOverrides.ContainsKey(c));
        btnClearAllTemps.Enabled = canEdit && grid!.InitialTemperatures.Count > 0;
        btnAllMaterial.Enabled = canEdit;
        btnSelectAll.Enabled = grid != null;
        btnClearSelection.Enabled = n > 0;

        UpdateSelectionSummary();
    }
}
