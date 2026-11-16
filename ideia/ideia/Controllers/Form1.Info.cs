using System.Linq;
using HeatTransferSim.Models;

namespace HeatTransferSim.Controllers;

// Textos do painel "Informações", do resumo dos blocos selecionados e da barra de status.
public partial class Form1
{
    private void UpdateMaterialInfoLabel(Material mat)
    {
        double alpha = mat.ThermalConductivity / (mat.Density * mat.SpecificHeat); // difusividade térmica (m²/s)
        lblMaterialInfo.Text =
            $"Material geral: {mat.Name}\r\n" +
            $"k = {mat.ThermalConductivity:G4} W/m·K    c = {mat.SpecificHeat:G4} J/kg·K\r\n" +
            $"ρ = {mat.Density:G4} kg/m³    α = {alpha:E2} m²/s";
    }

    /// <param name="full">true = atualiza também os textos que só mudam quando a configuração muda.</param>
    private void UpdateInfo(bool full = false)
    {
        var grid = _grid;
        if (grid == null)
        {
            lblTime.Text = "Tempo simulado: 0.00 s";
            lblDt.Text = "dt: —";
            lblBlocksInfo.Text = "Blocos: — (crie a grade)";
            lblComposition.Text = "Composição: —";
            lblEquilibriumInfo.Text = "T° final prevista: —";
            lblConvergence.Text = "Convergência: —";
            if (cboMaterial.SelectedItem is Material selected) UpdateMaterialInfoLabel(selected);
            UpdateSpeedLabel();
            return;
        }

        var (tMin, tMax) = grid.GetTemperatureRange();
        double spread = tMax - tMin;
        bool atEquilibrium = spread < HeatGrid.EquilibriumTolerance;
        if (grid.StepsComputed == 0) _initialSpread = spread;

        lblTime.Text = $"Tempo simulado: {FormatSimTime(grid.Time)}\r\nPassos calculados: {grid.StepsComputed:N0}";
        lblDt.Text = $"dt (passo de integração): {grid.SafeTimeStep:F4} s";
        UpdateSpeedLabel();

        double convergence = _initialSpread > 0 ? Math.Clamp(1.0 - spread / _initialSpread, 0.0, 1.0) : 1.0;
        lblConvergence.Text = atEquilibrium
            ? "Convergência: 100% — equilíbrio térmico"
            : $"Convergência até o equilíbrio: {convergence * 100:F2}%\r\nT° mín {tMin:F2} °C — máx {tMax:F2} °C";

        if (full)
        {
            UpdateMaterialInfoLabel(grid.DefaultMaterial);

            lblBlocksInfo.Text =
                $"Blocos: {grid.Rows * grid.Cols} ({grid.Rows} linhas × {grid.Cols} colunas)\r\n" +
                $"Com temperatura inicial própria: {grid.InitialTemperatures.Count}\r\n" +
                $"Com material próprio: {grid.MaterialOverrides.Count}";

            lblComposition.Text = "Composição da grade:\r\n" +
                string.Join("\r\n", grid.GetMaterialComposition().Select(x => $"   {x.material.Name}: {x.count} bloco(s)"));

            // Média ponderada pela capacidade térmica (m·c) de cada bloco: com materiais diferentes
            // a temperatura final NÃO é a média simples das temperaturas.
            lblEquilibriumInfo.Text =
                $"T° final prevista (equilíbrio): {grid.GetEquilibriumTemperature():F2} °C\r\n" +
                "(média ponderada pela capacidade térmica m·c de cada bloco)";
        }

        string baseMsg = $"Grade {grid.Rows}×{grid.Cols} | T° min = {tMin:F2} °C   max = {tMax:F2} °C";
        string state = atEquilibrium ? "Equilíbrio térmico atingido."
            : _isRunning ? "Transferência de calor em andamento..."
            : grid.StepsComputed == 0 ? "Pronta — clique em Iniciar."
            : "Pausada.";
        lblStatus.Text = $"{baseMsg} | {state}";

        UpdateSelectionSummary();
    }

    /// <summary>Resumo (ao vivo) dos blocos selecionados: material, temperatura inicial e temperatura atual.</summary>
    private void UpdateSelectionSummary()
    {
        var grid = _grid;
        var cells = _gridCtrl.SelectedCells;

        if (grid == null)
        {
            lblSelection.Text = "Crie a grade para poder selecionar blocos.";
            return;
        }
        if (cells.Count == 0)
        {
            lblSelection.Text = "Nenhum bloco selecionado.\r\nClique, arraste, ou clique nos números das linhas/colunas da grade.";
            return;
        }

        var bodies = cells.Select(c => grid.Bodies[c.row, c.col]).ToList();

        if (bodies.Count == 1)
        {
            Body b = bodies[0];
            bool ownMaterial = grid.MaterialOverrides.ContainsKey((b.Row, b.Col));
            string initial = grid.InitialTemperatures.TryGetValue((b.Row, b.Col), out double t0)
                ? $"{t0:F1} °C (personalizada)"
                : $"{HeatGrid.BaseTemperature:F1} °C (padrão)";
            lblSelection.Text =
                $"Bloco ({b.Row}, {b.Col})\r\n" +
                $"Material: {b.Material.Name} ({(ownMaterial ? "próprio" : "geral")})\r\n" +
                $"T° inicial: {initial}\r\n" +
                $"T° atual: {b.Temperature:F2} °C";
            return;
        }

        double min = bodies.Min(b => b.Temperature);
        double max = bodies.Max(b => b.Temperature);
        double avg = bodies.Average(b => b.Temperature);
        string materials = string.Join(", ",
            bodies.GroupBy(b => b.Material.Name).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ×{g.Count()}"));

        lblSelection.Text =
            $"{bodies.Count} blocos selecionados\r\n" +
            $"T° atual: {min:F2} a {max:F2} °C (média {avg:F2})\r\n" +
            $"Materiais: {Shorten(materials, 90)}";
    }

    /// <summary>Tempo simulado em unidade legível (s, min, h, dias).</summary>
    private static string FormatSimTime(double seconds)
    {
        if (seconds < 120) return $"{seconds:F2} s";
        string pretty = seconds < 7200 ? $"{seconds / 60:F1} min"
            : seconds < 172800 ? $"{seconds / 3600:F2} h"
            : $"{seconds / 86400:F2} dias";
        return $"{pretty} ({seconds:F0} s)";
    }

    /// <summary>Duração curta para o rótulo de velocidade (s, min, h, dias).</summary>
    private static string FormatRate(double seconds) =>
        seconds < 120 ? $"{seconds:F1} s"
        : seconds < 7200 ? $"{seconds / 60:F1} min"
        : seconds < 172800 ? $"{seconds / 3600:F1} h"
        : $"{seconds / 86400:F1} dias";

    private static string Shorten(string? message, int maxLen = 80)
    {
        if (string.IsNullOrWhiteSpace(message)) return "erro desconhecido";
        return message.Length <= maxLen ? message : message[..maxLen] + "...";
    }
}
