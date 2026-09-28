using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using HeatTransferSim.Models;
using HeatTransferSim.Views;

namespace HeatTransferSim.Controllers;

/// Simulador de transferência de calor entre corpos cúbicos em uma grade N x N.
/// Física:
///   - Transferência entre corpos adjacentes via Lei de Fourier: q = k·A·ΔT (Δx desconsiderado).
///   - Blocos cardinais (cima/baixo/esquerda/direita) têm w=1; diagonais têm w=0.25 (menor contato).
///   - Todos os blocos compartilham o mesmo material (escolha única/global).
///   - Calor sensível: Q = m·c·ΔT  ⇒  ΔT = Q / (m·c).
///   - Qualquer bloco pode receber, uma única vez, uma temperatura inicial (clique nele na
///     grade e aplique o valor no painel "Bloco selecionado"). Daí em diante a simulação só
///     transfere calor por condução. NÃO há fonte de calor contínua: a energia total da grade
///     é conservada, então a temperatura converge para um equilíbrio e nunca cresce indefinidamente.
public partial class Form1 : Form
{
    // Tamanho da grade fixo (não exposto na interface). Mude aqui se quiser outra grade.
    private const int GridSize = 5;

    private HeatGrid? _grid;
    private Body? _selectedBody;
    private readonly TemperatureGridControl _gridCtrl;
    private readonly System.Windows.Forms.Timer _timer;

    // ===== Controles: configuração =====
    private readonly NumericUpDown numSide = MkNum(0.001, 100, 0.01, 0.05, 3);
    private readonly ComboBox cboMaterial = new() { DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name", Width = 160 };
    private readonly Button btnCreate = new() { Text = "Criar / Recriar grade", AutoSize = true };

    // ===== Controles: bloco selecionado =====
    private readonly Label lblSelectedBlock = new() { Text = "Bloco selecionado: (nenhum)", AutoSize = true };
    private readonly NumericUpDown numBlockTemp = MkNum(-273, 1500, 10, 300);
    private readonly Button btnApplyBlockTemp = new() { Text = "Aplicar temperatura ao bloco", AutoSize = true };

    // ===== Controles: simulação =====
    private readonly Button btnStep = new() { Text = "Passo (1 dt)", AutoSize = true };
    private readonly Button btnReset = new() { Text = "Zerar temperaturas", AutoSize = true };
    private readonly Button btnPlay = new() { Text = "Iniciar", AutoSize = true };
    private readonly TrackBar sldSpeed = new() { Minimum = 1, Maximum = 500, Value = 30, TickFrequency = 10, Width = 140 };
    private readonly Label lblTime = new() { Text = "Tempo: 0.00 s", AutoSize = true };
    private readonly Label lblDt = new() { Text = "dt: 0.00 s", AutoSize = true };
    private readonly Label lblStatus = new() { Text = "Configure e clique em \"Criar\".", AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) };

    public Form1()
    {
        InitializeComponent();
        _timer = new System.Windows.Forms.Timer { Interval = 33 };
        _timer.Tick += Timer_Tick;
        _gridCtrl = new TemperatureGridControl { Dock = DockStyle.Fill };
        _gridCtrl.BodySelected += (b) => SelectBody(b);
        BuildGui();
        PopulateMaterials();
    }

    private void BuildGui()
    {
        // ---- Grupo: configuração ----
        var tCfg = MakePairPanel(
            ("Lado do cubo (m):", numSide),
            ("Material (todos os blocos):", cboMaterial));
        btnCreate.Margin = new Padding(0, 6, 0, 0);
        tCfg.Controls.Add(btnCreate, 0, tCfg.RowCount);
        tCfg.SetColumnSpan(btnCreate, 2);
        tCfg.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var grpCfg = new GroupBox { Text = "Configuração", Padding = new Padding(8), AutoSize = true };
        grpCfg.Controls.Add(tCfg);

        // ---- Grupo: bloco selecionado ----
        var tBlock = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
        tBlock.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tBlock.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        lblSelectedBlock.Margin = new Padding(0, 0, 0, 4);
        tBlock.Controls.Add(lblSelectedBlock, 0, 0);
        tBlock.SetColumnSpan(lblSelectedBlock, 2);
        tBlock.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        tBlock.Controls.Add(new Label { Text = "Temperatura (°C):", AutoSize = true }, 0, 1);
        tBlock.Controls.Add(numBlockTemp, 1, 1);
        tBlock.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        btnApplyBlockTemp.Margin = new Padding(0, 6, 0, 0);
        tBlock.Controls.Add(btnApplyBlockTemp, 0, 2);
        tBlock.SetColumnSpan(btnApplyBlockTemp, 2);
        tBlock.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var grpBlock = new GroupBox { Text = "Bloco selecionado (clique num bloco na grade)", Padding = new Padding(8), AutoSize = true };
        grpBlock.Controls.Add(tBlock);

        // ---- Grupo: simulação ----
        var tSim = MakePairPanel(("Velocidade (passos/quadro):", sldSpeed));
        btnStep.Margin = new Padding(0, 0, 4, 0);
        tSim.Controls.Add(btnStep, 0, 0);
        btnReset.Margin = new Padding(4, 0, 4, 0);
        tSim.Controls.Add(btnReset, 1, 0);
        btnPlay.Margin = new Padding(0, 0, 0, 0);
        tSim.Controls.Add(btnPlay, 0, 1);
        lblTime.Margin = new Padding(0, 4, 0, 0);
        tSim.Controls.Add(lblTime, 1, 1);
        lblDt.Margin = new Padding(0, 0, 0, 0);
        tSim.Controls.Add(lblDt, 0, 2);
        var grpSim = new GroupBox { Text = "Simulação", Padding = new Padding(8), AutoSize = true };
        grpSim.Controls.Add(tSim);

        var flpTop = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = true,
            Padding = new Padding(6),
        };
        flpTop.Controls.AddRange(new Control[] { grpCfg, grpBlock, grpSim });

        var panelBottom = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(6) };
        lblStatus.Dock = DockStyle.Fill;
        lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        panelBottom.Controls.Add(lblStatus);

        Controls.Add(_gridCtrl);
        Controls.Add(panelBottom);
        Controls.Add(flpTop);

        // Eventos
        btnCreate.Click += (s, e) => CreateGrid();
        btnStep.Click += (s, e) => SingleStep();
        btnReset.Click += (s, e) => ResetTemperatures();
        btnPlay.Click += (s, e) => TogglePlay();
        btnApplyBlockTemp.Click += (s, e) => ApplyBlockTemperature();
        cboMaterial.SelectedIndexChanged += (s, e) => MaterialChanged();
        numSide.ValueChanged += (s, e) => SideChanged();
        sldSpeed.ValueChanged += (s, e) => { };

        UpdateSelectionUI(true);
        UpdateInfo(true);
    }

    private void PopulateMaterials()
    {
        cboMaterial.Items.Clear();
        foreach (var m in Materials.All) cboMaterial.Items.Add(m);
        cboMaterial.SelectedIndex = 0;
    }

    private void CreateGrid()
    {
        double side = Decimal.ToDouble(numSide.Value);
        var material = cboMaterial.SelectedItem as Material ?? Materials.All[0];

        // Mantém as temperaturas iniciais já pintadas na grade anterior (se houver);
        // na primeiríssima vez, começa com um bloco quente padrão em (0,0), só pra
        // já ter algo acontecendo assim que você clicar em "Iniciar".
        IReadOnlyDictionary<(int row, int col), double> initial = _grid?.InitialTemperatures
            ?? new Dictionary<(int row, int col), double> { [(0, 0)] = 300.0 };

        _grid = new HeatGrid(GridSize, side, material, initial);
        _gridCtrl.Grid = _grid;
        _selectedBody = null;
        UpdateSelectionUI(true);
        UpdateInfo(true);
        lblStatus.Text = $"Grade {GridSize}×{GridSize} criada (cubos de lado {side} m).";
    }

    private void SingleStep()
    {
        if (_grid == null) return;
        _grid.Step();
        _gridCtrl.RefreshGrid();
        UpdateInfo();
    }

    private void Timer_Tick(object? s, EventArgs e)
    {
        if (_grid == null) return;
        int steps = sldSpeed.Value;
        for (int i = 0; i < steps; i++)
            _grid.Step(); // só redistribui calor — nada de fonte contínua aqui
        _gridCtrl.RefreshGrid();
        UpdateInfo();
    }

    private void TogglePlay()
    {
        if (_timer.Enabled)
        {
            _timer.Stop();
            btnPlay.Text = "Iniciar";
        }
        else
        {
            if (_grid == null) CreateGrid();
            _timer.Start();
            btnPlay.Text = "Pausa";
        }
    }

    private void ResetTemperatures()
    {
        if (_grid == null) return;
        _timer.Stop();
        btnPlay.Text = "Iniciar";
        _grid.ResetTemperatures();
        _gridCtrl.RefreshGrid();
        UpdateInfo(true);
    }

    private void MaterialChanged()
    {
        if (_grid == null) return;
        var mat = cboMaterial.SelectedItem as Material ?? Materials.All[0];
        _grid.SetMaterialForAll(mat);
        _gridCtrl.RefreshGrid();
        UpdateInfo(true);
    }

    private void SideChanged()
    {
        if (_grid == null) return;
        try { _grid.SetSide(Decimal.ToDouble(numSide.Value)); }
        catch { }
        _gridCtrl.RefreshGrid();
        UpdateInfo(true);
    }

    private void SelectBody(Body body)
    {
        _selectedBody = body;
        numBlockTemp.Value = (decimal)Math.Clamp(Math.Round(body.Temperature, 2), -273, 1500);
        UpdateSelectionUI();
    }

    private void ApplyBlockTemperature()
    {
        if (_grid == null || _selectedBody == null) return;
        double value = Decimal.ToDouble(numBlockTemp.Value);
        _grid.SetInitialTemperature(_selectedBody.Row, _selectedBody.Col, value);
        _gridCtrl.RefreshGrid();
        UpdateInfo();
    }

    private void UpdateSelectionUI(bool reset = false)
    {
        if (reset || _selectedBody == null)
        {
            lblSelectedBlock.Text = "Bloco selecionado: (nenhum)";
            numBlockTemp.Enabled = false;
            btnApplyBlockTemp.Enabled = false;
        }
        else
        {
            var b = _selectedBody;
            lblSelectedBlock.Text = $"Bloco selecionado: ({b.Row}, {b.Col})";
            numBlockTemp.Enabled = true;
            btnApplyBlockTemp.Enabled = true;
        }
    }

    private void UpdateInfo(bool full = false)
    {
        if (_grid == null)
        {
            lblTime.Text = "Tempo: 0.00 s";
            lblDt.Text = "dt: —";
            lblStatus.Text = "Configure e clique em \"Criar\".";
            return;
        }
        lblTime.Text = $"Tempo: {_grid.Time:F2} s";
        lblDt.Text = $"dt: {_grid.SafeTimeStep:F4} s";
        var (tMin, tMax) = _grid.GetTemperatureRange();
        string baseMsg = $"Grade {_grid.Size}×{_grid.Size} | T° min={tMin:F2}°C  max={tMax:F2}°C";
        if (Math.Abs(tMax - tMin) < 1e-6)
            lblStatus.Text = $"{baseMsg} | Equilíbrio térmico atingido.";
        else
            lblStatus.Text = $"{baseMsg} | Transferência de calor em andamento...";
    }

    private static TableLayoutPanel MakePairPanel(params (string label, Control ctrl)[] pairs)
    {
        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int i = 0; i < pairs.Length; i++)
        {
            t.Controls.Add(new Label { Text = pairs[i].label, AutoSize = true }, 0, i);
            t.Controls.Add(pairs[i].ctrl, 1, i);
        }
        t.RowCount = pairs.Length;
        for (int i = 0; i < pairs.Length; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return t;
    }

    private static NumericUpDown MkNum(double min, double max, double inc, double value, int decimals = 0) =>
        new() { Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)inc, Value = (decimal)value, DecimalPlaces = decimals, Width = 90 };
}
