using System.Linq;
using System.Threading.Tasks;
using HeatTransferSim.Models;
using MySqlConnector;

namespace HeatTransferSim.Data;

/// <summary>
/// Acesso ao banco MySQL: cria o esquema (tabelas) se ainda não existir e registra TODAS as
/// simulações. Cada vez que a grade é criada ou zerada começa uma simulação nova, que vira uma
/// linha própria em <c>simulations</c>, junto com:
///   - os blocos que receberam temperatura inicial customizada (<c>simulation_initial_temperatures</c>);
///   - os blocos que receberam material próprio (<c>simulation_block_materials</c>);
///   - e, conforme ela roda, o progresso (passos, tempo simulado, faixa de temperatura) até o equilíbrio.
/// </summary>
/// <remarks>
/// Todas as operações são assíncronas (ADO.NET async: OpenAsync/ExecuteNonQueryAsync/...), pelo mesmo
/// motivo do cálculo assíncrono das temperaturas em <see cref="HeatGrid.StepManyAsync"/>: conversar com
/// o banco não pode travar a interface. Qualquer falha (MySQL desligado, senha errada, tabela ausente...)
/// é propagada como exceção comum — quem chama decide o que fazer (ver Controllers/Form1.Database.cs:
/// o simulador continua funcionando e só avisa que não está registrando).
/// </remarks>
public static class SimulationRepository
{
    /// <summary>
    /// Cria as tabelas do banco caso ainda não existam e atualiza bancos criados pela versão anterior
    /// (grade quadrada, coluna GridSize). Chamado uma vez, ao abrir a janela.
    /// </summary>
    public static async Task EnsureSchemaAsync()
    {
        await using var conn = new MySqlConnection(DbConfig.ConnectionString);
        await conn.OpenAsync();

        await ExecuteAsync(conn, @"
            CREATE TABLE IF NOT EXISTS simulations (
                Id                   INT AUTO_INCREMENT PRIMARY KEY,
                StartedAt            DATETIME NOT NULL,
                FinishedAt           DATETIME NULL,
                GridRows             INT NOT NULL,
                GridCols             INT NOT NULL,
                CubeSideMeters       DOUBLE NOT NULL,
                MaterialName         VARCHAR(60) NOT NULL,
                ThermalConductivity  DOUBLE NOT NULL,
                SpecificHeat         DOUBLE NOT NULL,
                Density              DOUBLE NOT NULL,
                SafeTimeStepSeconds  DOUBLE NOT NULL,
                StepsComputed        BIGINT NOT NULL DEFAULT 0,
                SimulatedTimeSeconds DOUBLE NOT NULL DEFAULT 0,
                MinTemperature       DOUBLE NULL,
                MaxTemperature       DOUBLE NULL,
                ReachedEquilibrium   TINYINT(1) NOT NULL DEFAULT 0
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");

        await ExecuteAsync(conn, @"
            CREATE TABLE IF NOT EXISTS simulation_initial_temperatures (
                Id            INT AUTO_INCREMENT PRIMARY KEY,
                SimulationId  INT NOT NULL,
                RowIndex      INT NOT NULL,
                ColIndex      INT NOT NULL,
                Temperature   DOUBLE NOT NULL,
                CONSTRAINT FK_SimTemps_Simulation FOREIGN KEY (SimulationId)
                    REFERENCES simulations (Id) ON DELETE CASCADE
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");

        await ExecuteAsync(conn, @"
            CREATE TABLE IF NOT EXISTS simulation_block_materials (
                Id                   INT AUTO_INCREMENT PRIMARY KEY,
                SimulationId         INT NOT NULL,
                RowIndex             INT NOT NULL,
                ColIndex             INT NOT NULL,
                MaterialName         VARCHAR(60) NOT NULL,
                ThermalConductivity  DOUBLE NOT NULL,
                SpecificHeat         DOUBLE NOT NULL,
                Density              DOUBLE NOT NULL,
                CONSTRAINT FK_SimMaterials_Simulation FOREIGN KEY (SimulationId)
                    REFERENCES simulations (Id) ON DELETE CASCADE
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");

        await MigrateLegacySchemaAsync(conn);
    }

    /// <summary>
    /// Versão anterior do programa guardava a grade quadrada na coluna GridSize. Se o banco ainda tem
    /// essa coluna, cria GridRows/GridCols, copia o valor antigo para as duas e remove GridSize.
    /// Num banco novo (ou já migrado) não faz nada.
    /// </summary>
    private static async Task MigrateLegacySchemaAsync(MySqlConnection conn)
    {
        if (!await ColumnExistsAsync(conn, "simulations", "GridSize")) return;

        if (!await ColumnExistsAsync(conn, "simulations", "GridRows"))
            await ExecuteAsync(conn,
                "ALTER TABLE simulations ADD COLUMN GridRows INT NOT NULL DEFAULT 0, ADD COLUMN GridCols INT NOT NULL DEFAULT 0;");

        await ExecuteAsync(conn, "UPDATE simulations SET GridRows = GridSize, GridCols = GridSize WHERE GridRows = 0;");
        await ExecuteAsync(conn, "ALTER TABLE simulations DROP COLUMN GridSize;");
    }

    private static async Task<bool> ColumnExistsAsync(MySqlConnection conn, string table, string column)
    {
        const string sql = @"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND COLUMN_NAME = @column;";
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@table", table);
        cmd.Parameters.AddWithValue("@column", column);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
    }

    private static async Task ExecuteAsync(MySqlConnection conn, string sql)
    {
        await using var cmd = new MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Quantas simulações já estão registradas no banco.</summary>
    public static async Task<int> CountSimulationsAsync()
    {
        await using var conn = new MySqlConnection(DbConfig.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand("SELECT COUNT(*) FROM simulations;", conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    /// <summary>
    /// Registra uma NOVA simulação com a configuração atual: tamanho da grade (linhas × colunas), lado
    /// do cubo, material geral e suas propriedades, e também as temperaturas iniciais customizadas e os
    /// materiais próprios de blocos. Tudo numa transação: ou grava tudo, ou nada. Retorna o Id gerado,
    /// para atualizar o progresso depois com <see cref="UpdateSimulationProgressAsync"/>.
    /// </summary>
    public static async Task<int> InsertSimulationAsync(HeatGrid grid)
    {
        await using var conn = new MySqlConnection(DbConfig.ConnectionString);
        await conn.OpenAsync();
        await using MySqlTransaction tx = await conn.BeginTransactionAsync();

        Material material = grid.DefaultMaterial;

        const string insertSim = @"
            INSERT INTO simulations
                (StartedAt, GridRows, GridCols, CubeSideMeters, MaterialName, ThermalConductivity,
                 SpecificHeat, Density, SafeTimeStepSeconds, StepsComputed, SimulatedTimeSeconds)
            VALUES
                (UTC_TIMESTAMP(), @rows, @cols, @side, @material, @k, @c, @rho, @dt, 0, 0);";

        int simulationId;
        await using (var cmd = new MySqlCommand(insertSim, conn, tx))
        {
            cmd.Parameters.AddWithValue("@rows", grid.Rows);
            cmd.Parameters.AddWithValue("@cols", grid.Cols);
            cmd.Parameters.AddWithValue("@side", grid.Side);
            cmd.Parameters.AddWithValue("@material", material.Name);
            cmd.Parameters.AddWithValue("@k", material.ThermalConductivity);
            cmd.Parameters.AddWithValue("@c", material.SpecificHeat);
            cmd.Parameters.AddWithValue("@rho", material.Density);
            cmd.Parameters.AddWithValue("@dt", grid.SafeTimeStep);
            await cmd.ExecuteNonQueryAsync();
            simulationId = (int)cmd.LastInsertedId;
        }

        // Temperaturas iniciais (um comando reaproveitado, só trocando os valores a cada bloco)
        if (grid.InitialTemperatures.Count > 0)
        {
            const string insertTemp = @"
                INSERT INTO simulation_initial_temperatures (SimulationId, RowIndex, ColIndex, Temperature)
                VALUES (@sim, @row, @col, @temp);";
            await using var cmd = new MySqlCommand(insertTemp, conn, tx);
            var pSim = cmd.Parameters.Add("@sim", MySqlDbType.Int32);
            var pRow = cmd.Parameters.Add("@row", MySqlDbType.Int32);
            var pCol = cmd.Parameters.Add("@col", MySqlDbType.Int32);
            var pTemp = cmd.Parameters.Add("@temp", MySqlDbType.Double);
            pSim.Value = simulationId;

            foreach (var kvp in grid.InitialTemperatures.OrderBy(k => k.Key.row).ThenBy(k => k.Key.col))
            {
                pRow.Value = kvp.Key.row;
                pCol.Value = kvp.Key.col;
                pTemp.Value = kvp.Value;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        // Materiais próprios de blocos (só os que o usuário configurou individualmente)
        if (grid.MaterialOverrides.Count > 0)
        {
            const string insertMaterial = @"
                INSERT INTO simulation_block_materials
                    (SimulationId, RowIndex, ColIndex, MaterialName, ThermalConductivity, SpecificHeat, Density)
                VALUES (@sim, @row, @col, @name, @k, @c, @rho);";
            await using var cmd = new MySqlCommand(insertMaterial, conn, tx);
            var pSim = cmd.Parameters.Add("@sim", MySqlDbType.Int32);
            var pRow = cmd.Parameters.Add("@row", MySqlDbType.Int32);
            var pCol = cmd.Parameters.Add("@col", MySqlDbType.Int32);
            var pName = cmd.Parameters.Add("@name", MySqlDbType.VarChar);
            var pK = cmd.Parameters.Add("@k", MySqlDbType.Double);
            var pC = cmd.Parameters.Add("@c", MySqlDbType.Double);
            var pRho = cmd.Parameters.Add("@rho", MySqlDbType.Double);
            pSim.Value = simulationId;

            foreach (var kvp in grid.MaterialOverrides.OrderBy(k => k.Key.row).ThenBy(k => k.Key.col))
            {
                pRow.Value = kvp.Key.row;
                pCol.Value = kvp.Key.col;
                pName.Value = kvp.Value.Name;
                pK.Value = kvp.Value.ThermalConductivity;
                pC.Value = kvp.Value.SpecificHeat;
                pRho.Value = kvp.Value.Density;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        await tx.CommitAsync();
        return simulationId;
    }

    /// <summary>
    /// Atualiza o progresso/resultado de uma simulação já registrada: passos computados, tempo simulado
    /// e a faixa de temperatura atual. Quando <paramref name="reachedEquilibrium"/> é true, também marca
    /// <c>ReachedEquilibrium = 1</c> e grava o instante de término.
    /// </summary>
    public static async Task UpdateSimulationProgressAsync(int simulationId, HeatGrid grid, bool reachedEquilibrium)
    {
        var (min, max) = grid.GetTemperatureRange();

        await using var conn = new MySqlConnection(DbConfig.ConnectionString);
        await conn.OpenAsync();

        string sql = reachedEquilibrium
            ? @"UPDATE simulations SET
                    StepsComputed = @steps, SimulatedTimeSeconds = @time,
                    MinTemperature = @min, MaxTemperature = @max,
                    ReachedEquilibrium = 1, FinishedAt = UTC_TIMESTAMP()
                WHERE Id = @id;"
            : @"UPDATE simulations SET
                    StepsComputed = @steps, SimulatedTimeSeconds = @time,
                    MinTemperature = @min, MaxTemperature = @max
                WHERE Id = @id;";

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@steps", grid.StepsComputed);
        cmd.Parameters.AddWithValue("@time", grid.Time);
        cmd.Parameters.AddWithValue("@min", min);
        cmd.Parameters.AddWithValue("@max", max);
        cmd.Parameters.AddWithValue("@id", simulationId);
        await cmd.ExecuteNonQueryAsync();
    }
}
