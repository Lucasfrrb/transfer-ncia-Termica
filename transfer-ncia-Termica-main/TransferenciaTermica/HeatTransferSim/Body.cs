namespace HeatTransferSim.Models;

/// Corpo físico (cubo) que compõe a grade de simulação.
/// Propriedades geométricas e de estado: posição, material, tamanho do lado, temperatura.

public sealed class Body
{
    public int Row { get; }
    public int Col { get; }

    /// Material atual (pode ser alterado após a criação).
    public Material Material { get; set; }

    private double _side; // L (m)
    /// Comprimento da aresta do cubo (m).
    public double Side
    {
        get => _side;
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value), "O lado deve ser positivo.");
            _side = value;
        }
    }

    /// Temperatura atual do corpo (°C).
    public double Temperature { get; set; }

    public Body(int row, int col, Material material, double side, double temperature)
    {
        Row = row;
        Col = col;
        Material = material;
        Side = side;
        Temperature = temperature;
    }

    /// Volume do cubo (m³).
    public double Volume => Side * Side * Side;

    /// Massa do corpo (kg) = V · ρ.
    public double Mass => Volume * Material.Density;

    /// Calor sensível específico: m · c (J/K).
    public double ThermalCapacitance => Mass * Material.SpecificHeat;

    /// Área da seção transversal / face de contato entre corpos adjacentes (m²).
    public double FaceArea => Side * Side;

    public override string ToString() =>
        $"Corpo ({Row},{Col}) | {Material} | T={Temperature:F2}°C | m={Mass:E3} kg";
}
