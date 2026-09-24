using System.Collections.Generic;

namespace HeatTransferSim.Models;

/// <summary>
/// Grade N x N de corpos e motor da simulação de transferência de calor.
/// Todos os corpos compartilham o mesmo material (escolha única/global).
/// Qualquer bloco pode receber sua própria temperatura inicial — clique num
/// bloco na grade e aplique a temperatura desejada, quantos blocos você quiser.
/// Blocos não configurados começam na temperatura base.
/// </summary>
/// <remarks>
/// Importante: NÃO existe fonte de calor contínua. As temperaturas iniciais são
/// aplicadas uma única vez (ao criar a grade, ao "zerar", ou quando você aplica
/// uma nova temperatura a um bloco); depois disso a simulação apenas redistribui
/// a energia já existente entre os corpos, por condução (Lei de Fourier). Como a
/// energia total do sistema é conservada, a temperatura converge para um
/// equilíbrio e NUNCA cresce indefinidamente.
/// </remarks>
public sealed class HeatGrid
{
    /// <summary>°C dos blocos que não tiveram uma temperatura inicial customizada.</summary>
    public const double BaseTemperature = 25.0;

    public int Size { get; }
    public double Side { get; private set; }

    /// <summary>Vetor bidimensional de corpos.</summary>
    public Body[,] Bodies { get; private set; }

    private readonly Dictionary<(int row, int col), double> _initialTemperatures;

    /// <summary>Posições e temperaturas iniciais atualmente configuradas (somente leitura).</summary>
    public IReadOnlyDictionary<(int row, int col), double> InitialTemperatures => _initialTemperatures;

    /// <summary>
    /// Referência fria fixa da escala de cores: a menor temperatura realmente presente na
    /// grade no momento em que ela foi criada, zerada, ou em que você aplicou uma nova
    /// temperatura a um bloco. Fica fixa durante a simulação (sem recalcular a cada quadro,
    /// pra não gerar saltos), mas também não fica "presa" para sempre em um valor antigo que
    /// já dissipou — cada ação de configuração atualiza a referência para o estado atual.
    /// </summary>
    public double ColdReference { get; private set; } = BaseTemperature;

    /// <summary>Referência quente fixa da escala de cores (ver <see cref="ColdReference"/>).</summary>
    public double HotReference { get; private set; } = BaseTemperature;

    /// <summary>Tempo simulado acumulado (s).</summary>
    public double Time { get; private set; }

    /// <summary>Passo de tempo estável calculado automaticamente (s).</summary>
    public double SafeTimeStep { get; private set; } = 1.0;

    private static readonly (int dr, int dc, double weight)[] Neighbors =
    {
        (-1,  0, 1.0), // cima
        ( 1,  0, 1.0), // baixo
        ( 0, -1, 1.0), // esquerda
        ( 0,  1, 1.0), // direita
        (-1, -1, 0.25), // diagonal superior-esquerda
        (-1,  1, 0.25), // diagonal superior-direita
        ( 1, -1, 0.25), // diagonal inferior-esquerda
        ( 1,  1, 0.25), // diagonal inferior-direita
    };

    public HeatGrid(int size, double side, Material material, IReadOnlyDictionary<(int row, int col), double>? initialTemperatures = null)
    {
        Size = size;
        Side = side;
        _initialTemperatures = initialTemperatures != null
            ? new Dictionary<(int row, int col), double>(initialTemperatures)
            : new Dictionary<(int row, int col), double>();
        Bodies = new Body[size, size];
        InitializeBodies(material);
        ComputeSafeTimeStep();
    }

    private void InitializeBodies(Material material)
    {
        Time = 0.0;
        Bodies = new Body[Size, Size];
        for (int r = 0; r < Size; r++)
        for (int c = 0; c < Size; c++)
            Bodies[r, c] = new Body(r, c, material, Side, BaseTemperature);

        ApplyInitialTemperatures();
        RefreshColorReference();
    }

    /// <summary>Reancora Cold/HotReference na temperatura mínima e máxima realmente presentes na grade agora.</summary>
    private void RefreshColorReference()
    {
        var (min, max) = GetTemperatureRange();
        ColdReference = Math.Min(BaseTemperature, min);
        HotReference = Math.Max(BaseTemperature, max);
    }

    private void ApplyInitialTemperatures()
    {
        foreach (var kvp in _initialTemperatures)
        {
            var (r, c) = kvp.Key;
            if (r >= 0 && r < Size && c >= 0 && c < Size)
                Bodies[r, c].Temperature = kvp.Value;
        }
    }

    /// <summary>
    /// Define (ou substitui) a temperatura inicial de um bloco específico e aplica
    /// imediatamente ao corpo. O valor fica registrado e é reaplicado sempre que a
    /// grade for recriada ou zerada, e também passa a fazer parte da escala de cores.
    /// </summary>
    public void SetInitialTemperature(int row, int col, double temperature)
    {
        _initialTemperatures[(row, col)] = temperature;
        if (row >= 0 && row < Size && col >= 0 && col < Size)
            Bodies[row, col].Temperature = temperature;
        RefreshColorReference();
    }

    /// <summary>Reinicia o tempo e restaura todos os blocos às temperaturas iniciais configuradas.</summary>
    public void ResetTemperatures()
    {
        Time = 0.0;
        foreach (Body b in Bodies) b.Temperature = BaseTemperature;
        ApplyInitialTemperatures();
        RefreshColorReference();
        ComputeSafeTimeStep();
    }

    /// <summary>Altera o material de TODOS os corpos da grade (material único/global).</summary>
    public void SetMaterialForAll(Material material)
    {
        foreach (Body b in Bodies) b.Material = material;
        ComputeSafeTimeStep();
    }

    /// <summary>Altera o tamanho (lado) de todos os cubos da grade (m).</summary>
    public void SetSide(double side)
    {
        Side = side;
        foreach (Body b in Bodies) b.Side = side;
        ComputeSafeTimeStep();
    }

    /// <summary>Calcula o passo de tempo estável (explícito, com margem de segurança).</summary>
    public double ComputeSafeTimeStep()
    {
        double area = Side * Side;
        double maxLambda = 0.0;

        for (int r = 0; r < Size; r++)
        for (int c = 0; c < Size; c++)
        {
            Body b = Bodies[r, c];
            if (b.ThermalCapacitance <= 0) continue;

            double lambda = 0.0;

            // Acoplamentos corpo-corpo (Lei de Fourier): q = k · A · ΔT (Δx desconsiderado)
            foreach (var (dr, dc, w) in Neighbors)
            {
                int nr = r + dr, nc = c + dc;
                if (nr < 0 || nr >= Size || nc < 0 || nc >= Size) continue;
                Body nb = Bodies[nr, nc];
                double kEff = Math.Min(b.Material.ThermalConductivity, nb.Material.ThermalConductivity);
                lambda += (kEff * area * w) / b.ThermalCapacitance;
            }

            if (lambda > maxLambda) maxLambda = lambda;
        }

        // Para o Euler explícito acoplado, |autovalor| ≤ ~2·maxλ  ⇒  dt ≤ 1/maxλ.
        // Usa fator de segurança 0.25 e limita ao máximo para animação.
        SafeTimeStep = maxLambda > 0 ? Math.Min(0.25 / maxLambda, 5.0) : 5.0;
        return SafeTimeStep;
    }

    /// <summary>
    /// Avança a simulação em um passo de tempo dt (ou no passo seguro).
    /// Apenas redistribui o calor já presente na grade — não há criação de energia aqui,
    /// então a grade caminha para o equilíbrio e para de mudar, em vez de aquecer para sempre.
    /// </summary>
    /// <remarks>
    /// - Fourier: q = k·A·ΔT·w, com w=1 para adjacentes e w=0.25 para diagonais.
    /// - Calor sensível: Q = m·c·ΔT  ⇒  ΔT = Q / (m·c).
    /// </remarks>
    public void Step(double? dt = null)
    {
        double h = dt ?? SafeTimeStep;
        double area = Side * Side;

        // 1) Calcula o calor trocado por cada corpo com base nas temperaturas atuais (explícito).
        var heatFlow = new double[Size, Size]; // calor líquido (J) adicionado a cada corpo

        for (int r = 0; r < Size; r++)
        for (int c = 0; c < Size; c++)
        {
            Body b = Bodies[r, c];
            double sumQ = 0.0;

            foreach (var (dr, dc, w) in Neighbors)
            {
                int nr = r + dr, nc = c + dc;
                if (nr < 0 || nr >= Size || nc < 0 || nc >= Size) continue;
                Body nb = Bodies[nr, nc];

                // k efetivo = menor condutividade entre os dois materiais (sempre igual, já que o material é único)
                double kEff = Math.Min(b.Material.ThermalConductivity, nb.Material.ThermalConductivity);

                // Taxa de calor (W) do vizinho para este corpo (diagonal tem 25% da eficiência)
                double dT = nb.Temperature - b.Temperature; // >0: calor entra
                double q = kEff * area * dT * w;                 // q = k·A·ΔT·w
                sumQ += q * h;                               // energia no intervalo dt
            }

            heatFlow[r, c] = sumQ;
        }

        // 2) Aplica a variação de temperatura em cada corpo (Q = m·c·ΔT  ⇒  ΔT = Q/(m·c))
        for (int r = 0; r < Size; r++)
        for (int c = 0; c < Size; c++)
        {
            Body b = Bodies[r, c];
            if (b.ThermalCapacitance <= 0) continue;
            b.Temperature += heatFlow[r, c] / b.ThermalCapacitance;
        }

        Time += h;
    }

    /// <summary>Extremos de temperatura atual da grade.</summary>
    public (double min, double max) GetTemperatureRange()
    {
        if (Bodies.Length == 0) return (0, 0);
        double min = Bodies[0, 0].Temperature, max = min;
        foreach (Body b in Bodies)
        {
            if (b.Temperature < min) min = b.Temperature;
            if (b.Temperature > max) max = b.Temperature;
        }
        return (min, max);
    }
}
