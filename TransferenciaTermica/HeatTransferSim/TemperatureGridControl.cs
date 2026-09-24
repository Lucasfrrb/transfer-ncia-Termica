using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using HeatTransferSim.Models;

namespace HeatTransferSim.Views;

/// <summary>
/// Controle visual da grade: desenha os cubos coloridos conforme a temperatura e
/// permite clicar em um bloco para selecioná-lo (e então definir sua temperatura
/// inicial pelo painel "Bloco selecionado" do formulário).
/// Blocos com temperatura inicial customizada ficam com borda amarela; o bloco
/// selecionado no momento fica com borda branca.
/// </summary>
public sealed class TemperatureGridControl : UserControl
{
    [Browsable(true)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public HeatGrid? Grid
    {
        get => _grid;
        set { _grid = value; SelectedBody = null; Invalidate(); }
    }
    private HeatGrid? _grid;

    /// <summary>Células com temperatura maior que isso mostram o valor numérico.</summary>
    [DefaultValue(12)]
    public int MaxCellsForText { get; set; } = 12;

    [Browsable(false)]
    public Body? SelectedBody { get; private set; }

    /// <summary>Disparado quando o usuário clica em um bloco da grade.</summary>
    public event Action<Body>? BodySelected;

    public TemperatureGridControl()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(32, 32, 32);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 8.25f);
    }

    private const int LegendBand = 30;

    private int ComputeCellPixel()
    {
        if (Grid == null || Grid.Size <= 0) return 48;
        int usableH = Math.Max(1, Height - LegendBand - 16);
        int w = Math.Max(1, (Width - 16) / Grid.Size);
        int h = Math.Max(1, usableH / Grid.Size);
        int cell = Math.Min(w, h);
        return Math.Min(Math.Max(cell, 20), 80);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawGrid(g);
    }

    private void DrawGrid(Graphics g)
    {
        if (Grid == null)
        {
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("Clique em \"Criar / Recriar grade\" para iniciar a simulação.", Font, Brushes.White, ClientRectangle, sf);
            return;
        }

        int n = Grid.Size;
        int cell = ComputeCellPixel();
        int margin = 8;
        int totalW = cell * n;
        int totalH = cell * n;
        int originX = (Width - totalW) / 2;
        int originY = Math.Max(margin, (Height - totalH) / 2);

        // Escala de cores FIXA e absoluta: fria = temperatura base, quente = a maior temperatura
        // inicial configurada entre os blocos. Só é recalculada quando você cria/zera a grade ou
        // aplica uma nova temperatura a um bloco — nunca a cada quadro — então a cor de cada bloco
        // reflete o quão quente ele está de verdade, mudando aos poucos, sem saltos.
        double cold = Grid.ColdReference;
        double hot = Grid.HotReference;
        double span = hot - cold;

        bool showText = n <= MaxCellsForText;
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        for (int r = 0; r < n; r++)
        for (int c = 0; c < n; c++)
        {
            Body b = Grid.Bodies[r, c];
            double norm = span > 1e-9 ? (b.Temperature - cold) / span : 0.5;
            Color back = TemperatureToColor(norm);

            bool isSelected = SelectedBody == b;
            bool isCustom = Grid.InitialTemperatures.ContainsKey((r, c));
            Color border = isSelected ? Color.White : isCustom ? Color.Yellow : Color.FromArgb(40, 40, 40);
            float borderWidth = isSelected ? 3f : isCustom ? 2f : 1f;

            var rect = new Rectangle(originX + c * cell, originY + r * cell, cell, cell);
            using var brush = new SolidBrush(back);
            using var pen = new Pen(border, borderWidth);
            pen.Alignment = PenAlignment.Inset;
            g.FillRectangle(brush, rect);
            g.DrawRectangle(pen, rect);

            if (showText)
            {
                using var textBrush = new SolidBrush(ContrastingText(back));
                g.DrawString($"{b.Temperature:F1}", Font, textBrush, rect, fmt);
            }
        }

        // Legenda de escala de cores (barra gradiente) no canto inferior direito
        DrawLegend(g, cold, hot);
    }

    private void DrawLegend(Graphics g, double tMin, double tMax)
    {
        // Banda fixa na parte inferior da grade (não sobrepõe células)
        int bandH = LegendBand;
        int bandTop = Height - bandH;
        if (bandTop < 0) bandTop = 0;

        int barW = 220, barH = 18;
        int x = (Width - barW) / 2;
        int y = bandTop + (bandH - barH) / 2;
        x = Math.Max(4, x);
        if (x + barW > Width) barW = Math.Max(40, Width - x - 4);

        using var brush = new LinearGradientBrush(
            new Point(x, y), new Point(x + barW, y),
            TemperatureToColor(0), TemperatureToColor(1));
        var rc = new Rectangle(x, y, barW, barH);
        g.FillRectangle(brush, rc);
        using var border = new Pen(Color.White, 1);
        g.DrawRectangle(border, rc);

        using var sf = new StringFormat { LineAlignment = StringAlignment.Center };
        sf.Alignment = StringAlignment.Far;
        g.DrawString($"{tMin:F1}°C", Font, Brushes.White, x, y + barH / 2, sf);
        sf.Alignment = StringAlignment.Center;
        g.DrawString($"{(tMin + tMax) / 2:F1}°C", Font, Brushes.White, x + barW / 2, y + barH / 2, sf);
        sf.Alignment = StringAlignment.Near;
        g.DrawString($"{tMax:F1}°C", Font, Brushes.White, x + barW, y + barH / 2, sf);
    }

    /// <summary>Converte uma temperatura normalizada [0,1] em cor (azul → ... → vermelho).</summary>
    public static Color TemperatureToColor(double value)
    {
        value = Math.Clamp(value, 0.0, 1.0);
        // paradas: azul(0) → ciano → verde → amarelo → vermelho(1)
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

    private static Color Lerp(Color a, Color b, double f)
    {
        return Color.FromArgb(
            (int)(a.R + (b.R - a.R) * f),
            (int)(a.G + (b.G - a.G) * f),
            (int)(a.B + (b.B - a.B) * f));
    }

    /// <summary>Cor do texto contrastante com o fundo.</summary>
    private static Color ContrastingText(Color bg)
    {
        double lum = 0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B;
        return lum > 140 ? Color.Black : Color.White;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        SelectCell(e.Location);
    }

    private void SelectCell(Point p)
    {
        if (Grid == null) return;
        int n = Grid.Size;
        int cell = ComputeCellPixel();
        int totalW = cell * n, totalH = cell * n;
        int originX = (Width - totalW) / 2;
        int originY = Math.Max(8, (Height - totalH) / 2);

        int c = (p.X - originX) / cell;
        int r = (p.Y - originY) / cell;
        if (r >= 0 && r < n && c >= 0 && c < n)
        {
            var body = Grid.Bodies[r, c];
            SelectedBody = body;
            Invalidate();
            BodySelected?.Invoke(body);
        }
    }

    /// <summary>Atualiza a visualização (chamar após cada passo da simulação).</summary>
    public void RefreshGrid() => Invalidate();
}
