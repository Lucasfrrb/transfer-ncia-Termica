using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HeatTransferSim.Models;

/// <summary>
/// Grade Linhas × Colunas de blocos (cubos) e motor da simulação de transferência de calor.
/// </summary>
/// <remarks>
/// MATERIAIS: existe um <see cref="DefaultMaterial"/> (o "material geral" da grade) e, além dele,
/// qualquer bloco pode ter o seu PRÓPRIO material (<see cref="MaterialOverrides"/>). É assim que
/// se monta, por exemplo, uma fila de material isolante no meio de uma grade de cobre.
///
/// FÍSICA (condução entre dois blocos vizinhos, com materiais possivelmente diferentes):
/// o calor atravessa meio bloco do material 1 e meio bloco do material 2, ou seja, duas
/// resistências térmicas em SÉRIE:
///
///     R = (d/2)/(k1·A) + (d/2)/(k2·A)      ⇒      G = 1/R = A · kh / d
///     kh = 2·k1·k2 / (k1 + k2)             (média harmônica das condutividades)
///
/// onde A = lado² é a área de contato e d a distância entre os centros (d = lado para vizinhos
/// de cima/baixo/esquerda/direita; d = lado·√2 para as diagonais, que ainda têm peso 0.25 na
/// área de contato). A média harmônica faz o material PIOR condutor mandar na troca: entre um
/// metal e um isolante, kh fica muito próximo do k do isolante — por isso uma fila de isolante
/// "segura" o calor. Com materiais iguais, kh = k (a fórmula clássica q = k·A·ΔT/d).
///
/// O calor que entra no bloco i é  Σ G_ij · (T_j − T_i)  (W). A temperatura muda por
/// ΔT_i = (calor · dt) / C_i, com C_i = m_i · c_i (capacitância térmica do bloco, que depende do
/// material dele). G_ij = G_ji, então a energia total Σ C_i·T_i é CONSERVADA: não há fonte de
/// calor contínua, a temperatura converge para o equilíbrio e NUNCA cresce para sempre.
///
/// CÁLCULO: os blocos de cada passo são calculados em paralelo a partir das temperaturas atuais;
/// os passos continuam sequenciais porque cada um depende do anterior. <see cref="StepManyAsync"/>
/// executa esse trabalho no thread pool, sem bloquear a interface. Quem usa esta classe NÃO deve
/// alterar a grade (materiais, lado, temperaturas) enquanto um passo está sendo calculado.
/// </remarks>
public sealed class HeatGrid
{
    /// <summary>°C dos blocos que não tiveram uma temperatura inicial customizada.</summary>
    public const double BaseTemperature = 25.0;

    /// <summary>Diferença máxima (°C) entre o bloco mais quente e o mais frio para considerar "equilíbrio térmico".</summary>
    public const double EquilibriumTolerance = 1e-3;

    /// <summary>Teto do passo de tempo (s): só para a animação não "pular" demais com isolantes.</summary>
    public const double MaxTimeStep = 60.0;

    /// <summary>
    /// true = Lei de Fourier completa, q = k·A·ΔT/d (tempos de aquecimento realistas).
    /// false = modelo simplificado antigo, sem a distância d (q = k·A·ΔT): a grade fica ~1/lado
    /// vezes mais lenta. Mude aqui se o professor preferir o modelo simplificado.
    /// </summary>
    private static readonly bool IncludeDistance = true;

    /// <summary>dt = StepSafetyFactor / λmax (λmax = maior taxa de troca de calor de um bloco, em 1/s).</summary>
    private const double StepSafetyFactor = 0.1;

    private const int NeighborCount = 8;
    private const int ParallelCellThreshold = 256;

    private static readonly (int dr, int dc, double weight)[] Neighbors =
    {
        (-1,  0, 1.0),  // cima
        ( 1,  0, 1.0),  // baixo
        ( 0, -1, 1.0),  // esquerda
        ( 0,  1, 1.0),  // direita
        (-1, -1, 0.25), // diagonal superior-esquerda
        (-1,  1, 0.25), // diagonal superior-direita
        ( 1, -1, 0.25), // diagonal inferior-esquerda
        ( 1,  1, 0.25), // diagonal inferior-direita
    };

    public int Rows { get; }
    public int Cols { get; }
    public double Side { get; private set; }

    /// <summary>Material geral: vale para todos os blocos que NÃO têm material próprio.</summary>
    public Material DefaultMaterial { get; private set; }

    /// <summary>Vetor bidimensional [linha, coluna] de corpos. Cada corpo guarda o material efetivo dele.</summary>
    public Body[,] Bodies { get; }

    private readonly Dictionary<(int row, int col), double> _initialTemperatures = new();
    private readonly Dictionary<(int row, int col), Material> _materialOverrides = new();

    /// <summary>Blocos com temperatura inicial customizada (somente leitura).</summary>
    public IReadOnlyDictionary<(int row, int col), double> InitialTemperatures => _initialTemperatures;

    /// <summary>Blocos com material próprio, diferente do geral (somente leitura).</summary>
    public IReadOnlyDictionary<(int row, int col), Material> MaterialOverrides => _materialOverrides;

    /// <summary>Tempo simulado acumulado (s).</summary>
    public double Time { get; private set; }

    /// <summary>Quantidade de passos (dt) já computados nesta grade.</summary>
    public long StepsComputed { get; private set; }

    /// <summary>Passo de tempo estável, calculado automaticamente (s).</summary>
    public double SafeTimeStep { get; private set; } = MaxTimeStep;

    // Acoplamentos pré-calculados (refeitos por RebuildCouplings sempre que material ou lado mudam).
    private double[] _invCapacitance = Array.Empty<double>();   // 1 / (m·c) de cada bloco
    private int[] _neighborIndex = Array.Empty<int>();          // índice do vizinho k do bloco i em [i*8 + k]
    private double[] _neighborConductance = Array.Empty<double>(); // G (W/K) entre o bloco i e o vizinho k

    public HeatGrid(int rows, int cols, double side, Material defaultMaterial,
                    IReadOnlyDictionary<(int row, int col), double>? initialTemperatures = null,
                    IReadOnlyDictionary<(int row, int col), Material>? materialOverrides = null)
    {
        if (rows < 1) throw new ArgumentOutOfRangeException(nameof(rows), "A grade precisa de ao menos 1 linha.");
        if (cols < 1) throw new ArgumentOutOfRangeException(nameof(cols), "A grade precisa de ao menos 1 coluna.");
        if (side <= 0) throw new ArgumentOutOfRangeException(nameof(side), "O lado deve ser positivo.");

        Rows = rows;
        Cols = cols;
        Side = side;
        DefaultMaterial = defaultMaterial;

        // Só guarda o que cabe na grade (ao recriar com um tamanho menor, o que sobra é descartado).
        if (initialTemperatures != null)
            foreach (var kv in initialTemperatures)
                if (InBounds(kv.Key.row, kv.Key.col)) _initialTemperatures[kv.Key] = kv.Value;
        if (materialOverrides != null)
            foreach (var kv in materialOverrides)
                if (InBounds(kv.Key.row, kv.Key.col)) _materialOverrides[kv.Key] = kv.Value;

        Bodies = new Body[rows, cols];
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        {
            Material m = _materialOverrides.TryGetValue((r, c), out var own) ? own : defaultMaterial;
            Bodies[r, c] = new Body(r, c, m, side, BaseTemperature);
        }

        ApplyInitialTemperatures();
        RebuildCouplings();
    }

    private bool InBounds(int row, int col) => row >= 0 && row < Rows && col >= 0 && col < Cols;

    private void ApplyInitialTemperatures()
    {
        foreach (var kv in _initialTemperatures)
            Bodies[kv.Key.row, kv.Key.col].Temperature = kv.Value;
    }

    // =====================================================================
    //  Temperaturas iniciais (aplicadas uma única vez; sem fonte contínua)
    // =====================================================================

    /// <summary>Define a temperatura inicial dos blocos informados (e já aplica no corpo).</summary>
    public void SetInitialTemperatures(IEnumerable<(int row, int col)> cells, double temperature)
    {
        foreach (var (r, c) in cells)
        {
            if (!InBounds(r, c)) continue;
            _initialTemperatures[(r, c)] = temperature;
            Bodies[r, c].Temperature = temperature;
        }
    }

    /// <summary>Remove a temperatura customizada dos blocos informados (voltam a nascer em BaseTemperature).</summary>
    public void ClearInitialTemperatures(IEnumerable<(int row, int col)> cells)
    {
        foreach (var (r, c) in cells)
        {
            if (!InBounds(r, c)) continue;
            if (_initialTemperatures.Remove((r, c))) Bodies[r, c].Temperature = BaseTemperature;
        }
    }

    /// <summary>Remove TODAS as temperaturas customizadas: todos os blocos voltam a BaseTemperature.</summary>
    public void ClearAllInitialTemperatures()
    {
        _initialTemperatures.Clear();
        foreach (Body b in Bodies) b.Temperature = BaseTemperature;
    }

    /// <summary>Reinicia o tempo e restaura todos os blocos às temperaturas iniciais configuradas.</summary>
    public void ResetTemperatures()
    {
        Time = 0.0;
        StepsComputed = 0;
        foreach (Body b in Bodies) b.Temperature = BaseTemperature;
        ApplyInitialTemperatures();
    }

    // =====================================================================
    //  Materiais: geral + por bloco
    // =====================================================================

    /// <summary>
    /// Troca o material GERAL. Afeta os blocos que não têm material próprio; os que você
    /// configurou individualmente (ex.: a fila de isolante) continuam como estão.
    /// </summary>
    public void SetDefaultMaterial(Material material)
    {
        DefaultMaterial = material;
        foreach (Body b in Bodies)
            if (!_materialOverrides.ContainsKey((b.Row, b.Col))) b.Material = material;
        RebuildCouplings();
    }

    /// <summary>Faz TODOS os blocos usarem este material (apaga os materiais individuais).</summary>
    public void SetAllMaterials(Material material)
    {
        DefaultMaterial = material;
        _materialOverrides.Clear();
        foreach (Body b in Bodies) b.Material = material;
        RebuildCouplings();
    }

    /// <summary>Dá aos blocos informados um material PRÓPRIO.</summary>
    public void SetBlockMaterials(IEnumerable<(int row, int col)> cells, Material material)
    {
        foreach (var (r, c) in cells)
        {
            if (!InBounds(r, c)) continue;
            _materialOverrides[(r, c)] = material;
            Bodies[r, c].Material = material;
        }
        RebuildCouplings();
    }

    /// <summary>Tira o material próprio dos blocos informados: eles voltam a seguir o material geral.</summary>
    public void ClearBlockMaterials(IEnumerable<(int row, int col)> cells)
    {
        foreach (var (r, c) in cells)
        {
            if (!InBounds(r, c)) continue;
            if (_materialOverrides.Remove((r, c))) Bodies[r, c].Material = DefaultMaterial;
        }
        RebuildCouplings();
    }

    /// <summary>Altera o lado de todos os cubos da grade (m).</summary>
    public void SetSide(double side)
    {
        if (side <= 0) throw new ArgumentOutOfRangeException(nameof(side), "O lado deve ser positivo.");
        Side = side;
        foreach (Body b in Bodies) b.Side = side;
        RebuildCouplings();
    }

    // =====================================================================
    //  Acoplamentos e passo de tempo
    // =====================================================================

    /// <summary>Média harmônica de duas condutividades (duas resistências em série).</summary>
    public static double HarmonicMean(double k1, double k2) =>
        (k1 <= 0 || k2 <= 0) ? 0.0 : 2.0 * k1 * k2 / (k1 + k2);

    /// <summary>
    /// Pré-calcula, para cada bloco e cada um dos 8 vizinhos, a condutância G (W/K) e o inverso da
    /// capacitância térmica; também recalcula o passo de tempo seguro. Vizinho fora da grade vira
    /// "ele mesmo com G = 0" (contribui zero), o que dispensa testes de borda no laço do passo.
    /// </summary>
    private void RebuildCouplings()
    {
        int n = Rows * Cols;
        var invC = new double[n];
        var nbIndex = new int[n * NeighborCount];
        var nbG = new double[n * NeighborCount];
        double area = Side * Side;
        double maxLambda = 0.0;

        for (int r = 0; r < Rows; r++)
        for (int c = 0; c < Cols; c++)
        {
            int i = r * Cols + c;
            Body b = Bodies[r, c];
            double cap = b.ThermalCapacitance;
            invC[i] = cap > 0 ? 1.0 / cap : 0.0;

            double sumG = 0.0;
            for (int k = 0; k < NeighborCount; k++)
            {
                var (dr, dc, w) = Neighbors[k];
                int nr = r + dr, nc = c + dc;
                int slot = i * NeighborCount + k;

                if (!InBounds(nr, nc)) { nbIndex[slot] = i; nbG[slot] = 0.0; continue; }

                Body nb = Bodies[nr, nc];
                double kh = HarmonicMean(b.Material.ThermalConductivity, nb.Material.ThermalConductivity);
                double g = w * area * kh;                               // A · kh · peso
                if (IncludeDistance)
                    g /= (dr != 0 && dc != 0) ? Side * Math.Sqrt(2.0) : Side; // ... / d

                nbIndex[slot] = nr * Cols + nc;
                nbG[slot] = g;
                sumG += g;
            }

            double lambda = sumG * invC[i];   // taxa (1/s) com que este bloco "reage" aos vizinhos
            if (lambda > maxLambda) maxLambda = lambda;
        }

        _invCapacitance = invC;
        _neighborIndex = nbIndex;
        _neighborConductance = nbG;

        // Euler explícito é ESTÁVEL para dt < ~1/λmax, mas como é um método de 1ª ordem ele também precisa
        // ser PRECISO: com 0.1/λmax o erro na fase mais rápida (cobre 5×5, degrau de ~275 °C) fica abaixo
        // de ~2 °C e some em segundos simulados; o equilíbrio final é exato (energia conservada).
        SafeTimeStep = maxLambda > 0 ? Math.Min(StepSafetyFactor / maxLambda, MaxTimeStep) : MaxTimeStep;
    }

    /// <summary>
    /// Calcula o próximo vetor de temperaturas de forma independente por bloco, lendo apenas o
    /// vetor atual. A troca entre vetores só ocorre quando todos os blocos terminam.
    /// </summary>
    private static void Advance(
        double[] current, double[] next, double h, double[] g, int[] nbIndex, double[] invC,
        ParallelOptions parallelOptions)
    {
        int n = current.Length;

        void CalculateCell(int i)
        {
            double ti = current[i];
            double flow = 0.0;
            int o = i * NeighborCount;
            for (int k = 0; k < NeighborCount; k++)
                flow += g[o + k] * (current[nbIndex[o + k]] - ti);
            next[i] = ti + flow * h * invC[i];
        }

        if (n >= ParallelCellThreshold)
            Parallel.For(0, n, parallelOptions, CalculateCell);
        else
            for (int i = 0; i < n; i++) CalculateCell(i);
    }

    private void RunSteps(int count, double h, CancellationToken cancellationToken)
    {
        int n = Rows * Cols;
        var t = new double[n];
        var next = new double[n];
        for (int r = 0; r < Rows; r++)
        for (int c = 0; c < Cols; c++)
            t[r * Cols + c] = Bodies[r, c].Temperature;

        double[] g = _neighborConductance;
        int[] nbIndex = _neighborIndex;
        double[] invC = _invCapacitance;
        var parallelOptions = new ParallelOptions { CancellationToken = cancellationToken };

        int done = 0;
        try
        {
            while (done < count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Advance(t, next, h, g, nbIndex, invC, parallelOptions);
                (t, next) = (next, t);
                done++;
            }
        }
        finally
        {
            // Grava de volta o que foi calculado (mesmo se cancelado no meio do lote).
            for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                Bodies[r, c].Temperature = t[r * Cols + c];
            Time += h * done;
            StepsComputed += done;
        }
    }

    /// <summary>Avança um passo de forma síncrona (bloqueia quem chama). Para o loop contínuo use <see cref="StepManyAsync"/>.</summary>
    public void Step(double? dt = null) => RunSteps(1, dt ?? SafeTimeStep, CancellationToken.None);

    /// <summary>Avança um passo em segundo plano (thread pool).</summary>
    public Task StepAsync(double? dt = null, CancellationToken cancellationToken = default) =>
        StepManyAsync(1, dt, cancellationToken);

    /// <summary>
    /// Avança <paramref name="count"/> passos de tempo numa tarefa em segundo plano (thread pool).
    /// As células de cada passo são calculadas em paralelo; os passos permanecem sequenciais porque
    /// cada um depende do resultado do anterior. A interface só é atualizada ao fim do lote.
    /// </summary>
    public Task StepManyAsync(int count, double? dt = null, CancellationToken cancellationToken = default)
    {
        double h = dt ?? SafeTimeStep;
        return Task.Run(() => RunSteps(count, h, cancellationToken), cancellationToken);
    }

    // =====================================================================
    //  Consultas
    // =====================================================================

    /// <summary>Extremos de temperatura atual da grade.</summary>
    public (double min, double max) GetTemperatureRange()
    {
        double min = Bodies[0, 0].Temperature, max = min;
        foreach (Body b in Bodies)
        {
            if (b.Temperature < min) min = b.Temperature;
            if (b.Temperature > max) max = b.Temperature;
        }
        return (min, max);
    }

    /// <summary>true quando o bloco mais quente e o mais frio diferem menos que <see cref="EquilibriumTolerance"/>.</summary>
    public bool IsAtEquilibrium
    {
        get
        {
            var (min, max) = GetTemperatureRange();
            return max - min < EquilibriumTolerance;
        }
    }

    /// <summary>
    /// Temperatura final de equilíbrio prevista: Σ(C_i·T_i) / Σ C_i. É uma média PONDERADA pela
    /// capacitância térmica (m·c) de cada bloco — blocos de materiais diferentes "pesam" diferente.
    /// Como a energia é conservada, o valor é o mesmo antes, durante e depois da simulação.
    /// </summary>
    public double GetEquilibriumTemperature()
    {
        double sumCT = 0.0, sumC = 0.0;
        foreach (Body b in Bodies)
        {
            double cap = b.ThermalCapacitance;
            sumCT += cap * b.Temperature;
            sumC += cap;
        }
        return sumC > 0 ? sumCT / sumC : BaseTemperature;
    }

    /// <summary>Quantos blocos usam cada material agora (do mais comum para o menos comum).</summary>
    public List<(Material material, int count)> GetMaterialComposition() =>
        Bodies.Cast<Body>()
              .GroupBy(b => b.Material)
              .Select(g => (material: g.Key, count: g.Count()))
              .OrderByDescending(x => x.count)
              .ToList();
}
