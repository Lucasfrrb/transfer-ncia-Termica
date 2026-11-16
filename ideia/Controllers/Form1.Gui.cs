using System.Drawing;
using System.Windows.Forms;

namespace HeatTransferSim.Controllers;

// Montagem da interface: painel de cima (grade/material, blocos selecionados, simulação),
// painel de informações à direita, barra de status embaixo e a grade no meio.
public partial class Form1
{
    private void BuildGui()
    {
        // ---- Grupo: grade e material geral ----
        var tCfg = MakePairPanel(
            ("Linhas:", numRows),
            ("Colunas:", numCols),
            ("Lado do cubo (m):", numSide),
            ("Material geral:", cboMaterial));
        AddFullRow(tCfg, btnAllMaterial, new Padding(0, 6, 0, 0));
        AddFullRow(tCfg, btnCreate, new Padding(0, 6, 0, 0));
        var grpCfg = new GroupBox { Text = "Grade e material geral", Padding = new Padding(8), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        tCfg.Dock = DockStyle.Fill; // docado: fica abaixo do título do GroupBox em vez de sobrepô-lo
        grpCfg.Controls.Add(tCfg);

        // ---- Grupo: blocos selecionados (temperatura e material de qualquer bloco) ----
        var tSel = new TableLayoutPanel { ColumnCount = 4, RowCount = 4, AutoSize = true };
        for (int i = 0; i < 4; i++) tSel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int i = 0; i < 4; i++) tSel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        lblSelection.Margin = new Padding(0, 0, 0, 6);
        tSel.Controls.Add(lblSelection, 0, 0);
        tSel.SetColumnSpan(lblSelection, 4);

        tSel.Controls.Add(MakeLabel("Temperatura (°C):"), 0, 1);
        tSel.Controls.Add(numBlockTemp, 1, 1);
        tSel.Controls.Add(btnApplyTemp, 2, 1);
        tSel.Controls.Add(btnClearTemp, 3, 1);

        tSel.Controls.Add(MakeLabel("Material:"), 0, 2);
        tSel.Controls.Add(cboBlockMaterial, 1, 2);
        tSel.Controls.Add(btnApplyBlockMaterial, 2, 2);
        tSel.Controls.Add(btnResetBlockMaterial, 3, 2);

        var flowSel = new FlowLayoutPanel
        {
            AutoSize = true, WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 6, 0, 0),
        };
        flowSel.Controls.AddRange(new Control[] { btnSelectAll, btnClearSelection, btnClearAllTemps });
        tSel.Controls.Add(flowSel, 0, 3);
        tSel.SetColumnSpan(flowSel, 4);

        foreach (var b in new[] { btnApplyTemp, btnClearTemp, btnApplyBlockMaterial, btnResetBlockMaterial })
            b.Margin = new Padding(4, 2, 0, 2);

        var grpSel = new GroupBox { Text = "Blocos selecionados (clique/arraste na grade)", Padding = new Padding(8), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        tSel.Dock = DockStyle.Fill;
        grpSel.Controls.Add(tSel);

        // ---- Grupo: simulação ----
        sldSpeed.ValueChanged += (s, e) => UpdateSpeedLabel();

        var flowBtns = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
        flowBtns.Controls.AddRange(new Control[] { btnStep, btnPlay, btnReset });

        var flowSim = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.TopDown };
        lblTime.Margin = new Padding(0, 6, 0, 0);
        flowSim.Controls.AddRange(new Control[] { lblSpeed, sldSpeed, flowBtns, lblTime, lblDt });
        var grpSim = new GroupBox { Text = "Simulação", Padding = new Padding(8), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        flowSim.Dock = DockStyle.Fill;
        grpSim.Controls.Add(flowSim);

        var flpTop = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = true,
            Padding = new Padding(6),
        };
        flpTop.Controls.AddRange(new Control[] { grpCfg, grpSel, grpSim });

        // ---- Painel da direita: informações úteis ----
        var flowInfo = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        foreach (var lbl in new[] { lblMaterialInfo, lblComposition, lblBlocksInfo, lblEquilibriumInfo, lblConvergence, lblDbStatus })
        {
            lbl.Margin = new Padding(0, 0, 0, 10);
            flowInfo.Controls.Add(lbl);
        }
        var grpInfo = new GroupBox { Text = "Informações", Dock = DockStyle.Fill, Padding = new Padding(8) };
        grpInfo.Controls.Add(flowInfo);
        var pnlInfo = new Panel { Dock = DockStyle.Right, Width = 340, Padding = new Padding(0, 0, 6, 0) };
        pnlInfo.Controls.Add(grpInfo);

        // ---- Barra de status ----
        var panelBottom = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(6) };
        lblStatus.Dock = DockStyle.Fill;
        lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        panelBottom.Controls.Add(lblStatus);

        // A ordem importa no Dock: o que é adicionado por último ocupa a borda primeiro.
        Controls.Add(_gridCtrl);
        Controls.Add(pnlInfo);
        Controls.Add(panelBottom);
        Controls.Add(flpTop);

        // ---- Dicas ao passar o mouse ----
        _tip.SetToolTip(numRows, "Número de linhas da grade (1 a 40). Clique em \"Criar grade\" para aplicar.");
        _tip.SetToolTip(numCols, "Número de colunas da grade (1 a 40). Ex.: 3 linhas × 1 coluna, 5 × 9...");
        _tip.SetToolTip(cboMaterial, "Material de todos os blocos que NÃO têm material próprio. Mudar aqui não desfaz os blocos que você configurou individualmente.");
        _tip.SetToolTip(btnAllMaterial, "Faz TODOS os blocos usarem o material geral e apaga os materiais individuais.");
        _tip.SetToolTip(btnApplyTemp, "Define a temperatura inicial dos blocos selecionados.");
        _tip.SetToolTip(btnClearTemp, "Tira a temperatura personalizada dos blocos selecionados (voltam a 25 °C).");
        _tip.SetToolTip(btnApplyBlockMaterial, "Dá aos blocos selecionados um material PRÓPRIO (ex.: uma fila de isolante).");
        _tip.SetToolTip(btnResetBlockMaterial, "Tira o material próprio dos blocos selecionados: eles voltam a usar o material geral.");
        _tip.SetToolTip(btnClearAllTemps, "Tira a temperatura personalizada de TODOS os blocos (todos voltam a 25 °C).");
        _tip.SetToolTip(btnSelectAll, "Seleciona todos os blocos (atalho: Ctrl+A na grade).");
        _tip.SetToolTip(btnClearSelection, "Limpa a seleção (atalho: Esc na grade).");
        _tip.SetToolTip(sldSpeed, "Escala logarítmica, de 1 a 1000 passos por quadro. Metais terminam em poucos minutos simulados; isolantes levam dias — aumente a velocidade para eles.");

        // ---- Eventos (toda operação que calcula passos ou usa o banco é assíncrona) ----
        btnCreate.Click += (s, e) => CreateGrid();
        btnAllMaterial.Click += (s, e) => ApplyDefaultMaterialToAll();
        btnStep.Click += (s, e) => SafeRun(SingleStepAsync);
        btnReset.Click += (s, e) => SafeRun(ResetTemperaturesAsync);
        btnPlay.Click += (s, e) => SafeRun(TogglePlayAsync);
        btnApplyTemp.Click += (s, e) => ApplyTemperatureToSelection();
        btnClearTemp.Click += (s, e) => ClearTemperatureOfSelection();
        btnClearAllTemps.Click += (s, e) => ClearAllTemperatures();
        btnApplyBlockMaterial.Click += (s, e) => ApplyMaterialToSelection();
        btnResetBlockMaterial.Click += (s, e) => ResetMaterialOfSelection();
        btnSelectAll.Click += (s, e) => _gridCtrl.SelectAll();
        btnClearSelection.Click += (s, e) => _gridCtrl.ClearSelection();
        cboMaterial.SelectedIndexChanged += (s, e) => DefaultMaterialChanged();
        numSide.ValueChanged += (s, e) => SideChanged();
        numRows.ValueChanged += (s, e) => UpdateCreateButtonText();
        numCols.ValueChanged += (s, e) => UpdateCreateButtonText();
    }

    /// <summary>Roda uma operação assíncrona a partir de um evento de botão, sem nunca derrubar o programa.</summary>
    private async void SafeRun(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { lblStatus.Text = $"Erro: {Shorten(ex.Message)}"; }
    }

    private static Label MakeLabel(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 6, 4) };

    private static void AddFullRow(TableLayoutPanel t, Control ctrl, Padding margin)
    {
        ctrl.Margin = margin;
        int row = t.RowCount;
        t.RowCount = row + 1;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(ctrl, 0, row);
        t.SetColumnSpan(ctrl, t.ColumnCount);
    }

    private static TableLayoutPanel MakePairPanel(params (string label, Control ctrl)[] pairs)
    {
        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int i = 0; i < pairs.Length; i++)
        {
            t.Controls.Add(MakeLabel(pairs[i].label), 0, i);
            t.Controls.Add(pairs[i].ctrl, 1, i);
        }
        t.RowCount = pairs.Length;
        for (int i = 0; i < pairs.Length; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return t;
    }

    private static NumericUpDown MkNum(double min, double max, double inc, double value, int decimals = 0) =>
        new()
        {
            DecimalPlaces = decimals,
            Minimum = (decimal)min,
            Maximum = (decimal)max,
            Increment = (decimal)inc,
            Value = (decimal)value,
            Width = 90,
        };
}
