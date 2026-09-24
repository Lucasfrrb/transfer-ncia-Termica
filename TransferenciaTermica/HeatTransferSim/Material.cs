using System.Drawing;

namespace HeatTransferSim.Models;

/// <summary>
/// Representa um material com suas propriedades termofísicas no SI.
/// </summary>
public sealed class Material
{
    public string Name { get; }
    public double ThermalConductivity { get; }   // k  (W/m.K)
    public double SpecificHeat { get; }           // c  (J/kg.K)
    public double Density { get; }              // ρ  (kg/m³)
    public Color DisplayColor { get; }

    public Material(string name, double thermalConductivity, double specificHeat,
                    double density, Color displayColor)
    {
        Name = name;
        ThermalConductivity = thermalConductivity;
        SpecificHeat = specificHeat;
        Density = density;
        DisplayColor = displayColor;
    }

    public override string ToString() => Name;
}

/// <summary>
/// Repositório de materiais pré-definidos (pelo menos 8).
/// </summary>
public static class Materials
{
    public static readonly Material[] All =
    {
        new("Prata",           429,   235,   10490, Color.Silver),
        new("Cobre",           401,   385,   8960,  Color.Orange),
        new("Ouro",            317,   129,   19320, Color.Gold),
        new("Alumínio",        237,   903,   2700,  Color.LightGray),
        new("Latão",           120,   380,   8500,  Color.FromArgb(205,149,75)),
        new("Aço",             50,    450,   7850,  Color.Gray),
        new("Chumbo",          35,    130,   11340, Color.DimGray),
        new("Gelo",            2.18,  2108,  917,   Color.LightBlue),
        new("Vidro",           0.95,  840,   2500,  Color.SkyBlue),
        new("Cerâmica",        0.8,   880,   1800,  Color.WhiteSmoke),
        new("Madeira (Noz)",   0.12,  1700,  700,   Color.SaddleBrown),
        new("Plástico (HDPE)", 0.2,   1400,  950,   Color.LimeGreen),
        new("Borracha",        0.16,  1500,  1100,  Color.DarkGreen),
    };
}
