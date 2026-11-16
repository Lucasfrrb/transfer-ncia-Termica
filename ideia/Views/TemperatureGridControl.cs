using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using HeatTransferSim.Models;

namespace HeatTransferSim.Views;

/// <summary>
/// Controle visual da grade Linhas × Colunas: desenha os cubos coloridos pela temperatura e permite
/// SELECIONAR blocos para o formulário aplicar temperatura e/ou material neles.
/// </summary>
/// <remarks>
/// Seleção:
///   - clique                 → seleciona um bloco
///   - clique e arraste       → seleciona um retângulo de blocos
///   - Ctrl + clique          → liga/desliga um bloco na seleção
///   - clique num número de linha/coluna (margens) → seleciona a fila inteira (Ctrl soma à seleção)
///   - clique no canto (esquerda-cima), ou Ctrl+A → seleciona tudo; Esc ou clique fora → limpa
///
/// Aparência: borda branca = selecionado; borda amarela = tem temperatura inicial customizada; faixa
/// colorida embaixo do bloco (com a sigla do material) = bloco com material próprio.
///
/// A escala de cores é FIXA e absoluta (<see cref="ColorScaleMin"/>/<see cref="ColorScaleMax"/>): a
/// mesma temperatura é sempre a mesma cor, em qualquer simulação, e nunca se ajusta à grade.
/// </remarks>
public sealed class TemperatureGridControl : UserControl
{
    /// <summary>Extremo frio da escala de cores fixa (°C) — azul.</summary>
    public const double ColorScaleMin = -200.0;

    /// <summary>Extremo quente da escala de cores fixa (°C) — vermelho.</summary>
    public const double ColorScaleMax = 1500.0;

    private const double ColorScaleSpan = ColorScaleMax - ColorScaleMin;

    private const int HeaderW = 26;     // faixa com os números das linhas (à esquerda da grade)
    private const int HeaderH = 20;     // faixa com os números das colunas (em cima da grade)
    private const int LegendBand = 54;  // faixa de baixo: linha de informação + barra de cores
    private const int Margin = 8;

    private HeatGrid? _grid;
    private readonly HashSet<(int row, int col)> _selected = new();
    private HashSet<(int row, int col)> _selectionBefore = new();
    private (int row, int col)? _hover;
    private (int row, int col) _anchor;
    private bool _dragging;
    private readonly Font _smallFont = new("Segoe UI", 7f);
    private readonly Font _headerFont = new("Segoe UI", 7.5f);

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public HeatGrid? Grid
    {
        get => _grid;
        set
        {
            _grid = value;
            _selected.Clear();
            _selectionBefore.Clear();
            _hover = null;
            _dragging = false;
            Invalidate();
        }
    }

    /// <summary>Blocos atualmente selecionados, como (linha, coluna).</summary>
    [Browsable(false)]
    public IReadOnlyCollection<(int row, int col)> SelectedCells => _selected;

    /// <summary>Disparado quando a seleção muda por ação do usuário (clique, arraste, Ctrl+A, Esc...).</summary>
    public event Action? SelectionChanged;

    public TemperatureGridControl()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = Color.FromArgb(32, 32, 32);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 8.25f);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _smallFont.Dispose();
            _headerFont.Dispose();
        }
        base.Dispose(disposing);
    }

    // =====================================================================
    //  Layout
    // =====================================================================

    /// <summary>Tamanho da célula em pixels e canto superior-esquerdo da grade (mesmo cálculo para desenhar e para o mouse).</summary>
    private (int cell, int originX, int originY) ComputeLayout(HeatGrid grid)
    {
        int availW = Math.Max(1, Width - HeaderW - 2 * Margin);
        int availH = Math.Max(1, Height - HeaderH - LegendBand - 2 * Margin);
        int cell = Math.Clamp(Math.Min(availW / grid.Cols, availH / grid.Rows), 6, 90);
        int originX = HeaderW + Margin + Math.Max(0, (availW - cell * grid.Cols) / 2);
        int originY = HeaderH + Margin + Math.Max(0, (availH - cell * grid.Rows) / 2);
        return (cell, originX, originY);
    }

    // =====================================================================
    //  Desenho
    // =====================================================================

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        var grid = Grid;

        if (grid == null)
        {
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("Defina linhas e colunas e clique em \"Criar grade\" para iniciar.", Font, Brushes.White, ClientRectangle, sf);
            return;
        }

        DrawGrid(g, grid);
        DrawInfoLine(g, grid);
        DrawLegend(g);
    }

    private void DrawGrid(Graphics g, HeatGrid grid)
    {
        var (cell, ox, oy) = ComputeLayout(grid);
        int rows = grid.Rows, cols = grid.Cols;
        bool showText = cell >= 40;
        int bandH = cell >= 40 ? 12 : Math.Max(4, cell / 5);

        using var fill = new SolidBrush(Color.Black);
        using var textBrush = new SolidBrush(Color.White);
        using var overlay = new SolidBrush(Color.FromArgb(70, 255, 255, 255));
        using var penNormal = new Pen(Color.FromArgb(40, 40, 40), 1f) { Alignment = PenAlignment.Inset };
        using var penCustom = new Pen(Color.Yellow, 2f) { Alignment = PenAlignment.Inset };
        using var penSelected = new Pen(Color.White, 2f) { Alignment = PenAlignment.Inset };
        using var center = new StringFormat(StringFormatFlags.NoWrap | StringFormatFlags.NoClip)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };

        DrawHeaders(g, grid, cell, ox, oy, center);

        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        {
            Body b = grid.Bodies[r, c];
            // Escala FIXA: ColorScaleMin..ColorScaleMax sempre vira azul..vermelho.
            Color back = TemperatureToColor((b.Temperature - ColorScaleMin) / ColorScaleSpan);
            var rect = new Rectangle(ox + c * cell, oy + r * cell, cell, cell);

            fill.Color = back;
            g.FillRectangle(fill, rect);

            bool ownMaterial = grid.MaterialOverrides.ContainsKey((r, c));
            if (ownMaterial)
            {
                var band = new Rectangle(rect.X + 1, rect.Bottom - bandH - 1, rect.Width - 2, bandH);
                fill.Color = b.Material.DisplayColor;
                g.FillRectangle(fill, band);
                if (bandH >= 11)
                {
                    textBrush.Color = ContrastingText(b.Material.DisplayColor);
                    g.DrawString(Abbreviate(b.Material.Name, band.Width), _smallFont, textBrush, band, center);
                }
            }

            if (showText)
            {
                textBrush.Color = ContrastingText(back);
                var textArea = new Rectangle(rect.X, rect.Y, rect.Width, rect.Height - (ownMaterial ? bandH + 1 : 0));
                g.DrawString($"{b.Temperature:F1}", Font, textBrush, textArea, center);
            }

            bool selected = _selected.Contains((r, c));
            if (selected) g.FillRectangle(overlay, rect);

            bool customTemp = grid.InitialTemperatures.ContainsKey((r, c));
            g.DrawRectangle(selected ? penSelected : customTemp ? penCustom : penNormal, rect);
        }
    }

    private void DrawHeaders(Graphics g, HeatGrid grid, int cell, int ox, int oy, StringFormat center)
    {
        int step = cell >= 16 ? 1 : cell >= 10 ? 2 : 5;
        var rowCount = new int[grid.Rows];
        var colCount = new int[grid.Cols];
        foreach (var (r, c) in _selected) { rowCount[r]++; colCount[c]++; }

        using var dim = new SolidBrush(Color.FromArgb(170, 170, 170));
        using var bright = new SolidBrush(Color.White);
        using var highlight = new SolidBrush(Color.FromArgb(90, 90, 90));

        for (int c = 0; c < grid.Cols; c += step)
        {
            var rc = new Rectangle(ox + c * cell, oy - HeaderH, cell, HeaderH - 2);
            bool full = colCount[c] == grid.Rows;
            if (full) g.FillRectangle(highlight, rc);
            g.DrawString(c.ToString(), _headerFont, full ? bright : dim, rc, center);
        }
        for (int r = 0; r < grid.Rows; r += step)
        {
            var rc = new Rectangle(ox - HeaderW, oy + r * cell, HeaderW - 2, cell);
            bool full = rowCount[r] == grid.Cols;
            if (full) g.FillRectangle(highlight, rc);
            g.DrawString(r.ToString(), _headerFont, full ? bright : dim, rc, center);
        }

        // Canto: "tudo"
        var corner = new Rectangle(ox - HeaderW, oy - HeaderH, HeaderW - 2, HeaderH - 2);
        g.DrawString("tudo", _smallFont, dim, corner, center);
    }

    private void DrawInfoLine(Graphics g, HeatGrid grid)
    {
        string text;
        if (_hover is { } h && h.row < grid.Rows && h.col < grid.Cols)
        {
            Body b = grid.Bodies[h.row, h.col];
            bool own = grid.MaterialOverrides.ContainsKey(h);
            text = $"({h.row}, {h.col})  ·  {b.Material.Name}{(own ? " (próprio)" : "")}  ·  " +
                   $"k = {b.Material.ThermalConductivity:G3} W/m·K  ·  {b.Temperature:F2} °C";
        }
        else if (_selected.Count > 0)
            text = $"{_selected.Count} bloco(s) selecionado(s)";
        else
            text = "Clique num bloco, arraste para selecionar vários, ou clique nos números das linhas/colunas.";

        using var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        g.DrawString(text, Font, Brushes.White, new Rectangle(4, Height - LegendBand, Math.Max(1, Width - 8), 22), sf);
    }

    private void DrawLegend(Graphics g)
    {
        int bandTop = Math.Max(0, Height - LegendBand);
        int barW = 220, barH = 18;
        int x = Math.Max(60, (Width - barW) / 2);
        int y = bandTop + 28;
        if (x + barW > Width - 60) barW = Math.Max(40, Width - 60 - x);

        var rc = new Rectangle(x, y, barW, barH);
        using (var brush = new LinearGradientBrush(new Point(x, y), new Point(x + barW, y),
                   TemperatureToColor(0), TemperatureToColor(1)))
            g.FillRectangle(brush, rc);
        using (var border = new Pen(Color.White, 1))
            g.DrawRectangle(border, rc);

        using var sf = new StringFormat { LineAlignment = StringAlignment.Center };
        sf.Alignment = StringAlignment.Far;
        g.DrawString($"{ColorScaleMin:F0} °C", Font, Brushes.White, x - 4, y + barH / 2, sf);
        sf.Alignment = StringAlignment.Center;
        g.DrawString($"{(ColorScaleMin + ColorScaleMax) / 2:F0} °C", Font, Brushes.Black, x + barW / 2, y + barH / 2, sf);
        sf.Alignment = StringAlignment.Near;
        g.DrawString($"{ColorScaleMax:F0} °C", Font, Brushes.White, x + barW + 4, y + barH / 2, sf);
    }

    /// <summary>Converte uma temperatura normalizada [0,1] em cor (azul → ciano → verde → amarelo → vermelho).</summary>
    public static Color TemperatureToColor(double value)
    {
        value = Math.Clamp(value, 0.0, 1.0);
        (double pos, Color color)[] stops =
        {
            (0.00, Color.FromArgb( 30,  30, 180)),
            (0.25, Color.FromArgb(  0, 180, 255)),
            (0.50, Color.FromArgb( 30, 200,  30)),
            (0.75, Color.FromArgb(255, 220,  40)),
            (1.00, Color.FromArgb(220,  30,  30)),
        };
        for (int i = 0; i < stops.Length - 1; i++)
        {
            if (value <= stops[i + 1].pos)
            {
                double f = (value - stops[i].pos) / (stops[i + 1].pos - stops[i].pos);
                return Lerp(stops[i].color, stops[i + 1].color, f);
            }
        }
        return stops[^1].color;
    }

    private static Color Lerp(Color a, Color b, double f) =>
        Color.FromArgb(
            (int)(a.R + (b.R - a.R) * f),
            (int)(a.G + (b.G - a.G) * f),
            (int)(a.B + (b.B - a.B) * f));

    /// <summary>Cor de texto que contrasta com o fundo.</summary>
    private static Color ContrastingText(Color bg)
    {
        double lum = 0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B;
        return lum > 140 ? Color.Black : Color.White;
    }

    /// <summary>Sigla do material que cabe na faixa: primeira palavra do nome, cortada pela largura.</summary>
    private static string Abbreviate(string name, int pixelWidth)
    {
        string word = name.Split(' ', '(')[0];
        int maxChars = Math.Max(1, pixelWidth / 6);
        return word.Length <= maxChars ? word : word[..maxChars];
    }

    // =====================================================================
    //  Mouse / teclado / seleção
    // =====================================================================

    private enum Hit { None, Cell, RowHeader, ColHeader, Corner }

    private Hit HitTest(Point p, out int row, out int col)
    {
        row = col = -1;
        var grid = Grid;
        if (grid == null) return Hit.None;

        var (cell, ox, oy) = ComputeLayout(grid);
        int c = (int)Math.Floor((p.X - ox) / (double)cell);
        int r = (int)Math.Floor((p.Y - oy) / (double)cell);
        bool inCols = c >= 0 && c < grid.Cols;
        bool inRows = r >= 0 && r < grid.Rows;
        bool inRowHeaderX = p.X >= ox - HeaderW && p.X < ox;
        bool inColHeaderY = p.Y >= oy - HeaderH && p.Y < oy;

        if (inCols && inRows) { row = r; col = c; return Hit.Cell; }
        if (inRowHeaderX && inRows) { row = r; return Hit.RowHeader; }
        if (inColHeaderY && inCols) { col = c; return Hit.ColHeader; }
        if (inRowHeaderX && inColHeaderY) return Hit.Corner;
        return Hit.None;
    }

    private static IEnumerable<(int row, int col)> RectCells(int r1, int c1, int r2, int c2)
    {
        int rMin = Math.Min(r1, r2), rMax = Math.Max(r1, r2);
        int cMin = Math.Min(c1, c2), cMax = Math.Max(c1, c2);
        for (int r = rMin; r <= rMax; r++)
        for (int c = cMin; c <= cMax; c++)
            yield return (r, c);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var grid = Grid;
        if (grid == null || e.Button != MouseButtons.Left) return;

        bool ctrl = (ModifierKeys & Keys.Control) == Keys.Control;
        _selectionBefore = new HashSet<(int row, int col)>(_selected);

        switch (HitTest(e.Location, out int row, out int col))
        {
            case Hit.Cell:
                if (ctrl)
                {
                    if (!_selected.Remove((row, col))) _selected.Add((row, col));
                }
                else
                {
                    _selected.Clear();
                    _selected.Add((row, col));
                    _anchor = (row, col);
                    _dragging = true;
                }
                break;
            case Hit.RowHeader:
                if (!ctrl) _selected.Clear();
                _selected.UnionWith(RectCells(row, 0, row, grid.Cols - 1));
                break;
            case Hit.ColHeader:
                if (!ctrl) _selected.Clear();
                _selected.UnionWith(RectCells(0, col, grid.Rows - 1, col));
                break;
            case Hit.Corner:
                _selected.Clear();
                _selected.UnionWith(RectCells(0, 0, grid.Rows - 1, grid.Cols - 1));
                break;
            default:
                if (!ctrl) _selected.Clear();
                break;
        }
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var grid = Grid;
        if (grid == null) return;

        if (_dragging)
        {
            var (cell, ox, oy) = ComputeLayout(grid);
            int c = Math.Clamp((int)Math.Floor((e.X - ox) / (double)cell), 0, grid.Cols - 1);
            int r = Math.Clamp((int)Math.Floor((e.Y - oy) / (double)cell), 0, grid.Rows - 1);
            _selected.Clear();
            _selected.UnionWith(RectCells(_anchor.row, _anchor.col, r, c));
            _hover = (r, c);
            Invalidate();
            return;
        }

        Hit hit = HitTest(e.Location, out int hr, out int hc);
        bool overHeader = hit == Hit.RowHeader || hit == Hit.ColHeader || hit == Hit.Corner;
        Cursor = overHeader ? Cursors.Hand : Cursors.Default;

        (int row, int col)? newHover = null;
        if (hit == Hit.Cell) newHover = (hr, hc);
        if (!Equals(newHover, _hover))
        {
            _hover = newHover;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;

        _dragging = false;
        if (!_selected.SetEquals(_selectionBefore))
        {
            _selectionBefore = new HashSet<(int row, int col)>(_selected);
            SelectionChanged?.Invoke();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != null && !_dragging)
        {
            _hover = null;
            Invalidate();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Grid == null) return;

        if (e.Control && e.KeyCode == Keys.A) { SelectAll(); e.Handled = true; }
        else if (e.KeyCode == Keys.Escape) { ClearSelection(); e.Handled = true; }
    }

    /// <summary>Seleciona todos os blocos.</summary>
    public void SelectAll()
    {
        var grid = Grid;
        if (grid == null) return;
        ReplaceSelection(new HashSet<(int row, int col)>(RectCells(0, 0, grid.Rows - 1, grid.Cols - 1)));
    }

    /// <summary>Limpa a seleção.</summary>
    public void ClearSelection() => ReplaceSelection(new HashSet<(int row, int col)>());

    private void ReplaceSelection(HashSet<(int row, int col)> next)
    {
        if (_selected.SetEquals(next)) return;
        _selected.Clear();
        _selected.UnionWith(next);
        _selectionBefore = new HashSet<(int row, int col)>(_selected);
        Invalidate();
        SelectionChanged?.Invoke();
    }

    /// <summary>Atualiza a visualização (chamar após cada passo da simulação ou mudança de configuração).</summary>
    public void RefreshGrid() => Invalidate();
}
