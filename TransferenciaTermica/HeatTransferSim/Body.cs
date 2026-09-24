namespace HeatTransferSim.Models;

/// <summary>
/// Corpo físico (cubo) que compõe a grade de simulação.
/// Propriedades geométricas e de estado: posição, material, tamanho do lado, temperatura.
/// </summary>
public sealed class Body
{
    public int Row { get; }
    public int Col { get; }

    /// <summary>Material atual (pode ser alterado após a criação).</summary>
    public Material Material { get; set; }

    private double _side; // L (m)
    /// <summary>Comprimento da aresta do cubo (m).</summary>
    public double Side
    {
        get => _side;
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value), "O lado deve ser positivo.");
            _side = value;
        }
    }

    /// <summary>Temperatura atual do corpo (°C).</summary>
    public double Temperature { get; set; }

    public Body(int row, int col, Material material, double side, double temperature)
    {
        Row = row;
        Col = col;
        Material = material;
        Side = side;
        Temperature = temperature;
    }

    /// <summary>Volume do cubo (m³).</summary>
    public double Volume => Side * Side * Side;

    /// <summary>Massa do corpo (kg) = V · ρ.</summary>
    public double Mass => Volume * Material.Density;

    /// <summary>Calor sensível específico: m · c (J/K).</summary>
    public double ThermalCapacitance => Mass * Material.SpecificHeat;

    /// <summary>Área da seção transversal / face de contato entre corpos adjacentes (m²).</summary>
    public double FaceArea => Side * Side;

    public override string ToString() =>
        $"Corpo ({Row},{Col}) | {Material} | T={Temperature:F2}°C | m={Mass:E3} kg";
}
