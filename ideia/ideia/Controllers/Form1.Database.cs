using System.Threading.Tasks;
using HeatTransferSim.Data;
using HeatTransferSim.Models;

namespace HeatTransferSim.Controllers;

// Registro das simulações no MySQL. Toda chamada ao banco é assíncrona e protegida: se o banco cair,
// o simulador continua funcionando e o painel "Informações" mostra o motivo.
public partial class Form1
{
    /// <summary>Confere a conexão e cria as tabelas (se faltarem). Roda em segundo plano logo ao abrir a janela.</summary>
    private async Task InitializeDatabaseAsync()
    {
        lblDbStatus.Text = "Banco: verificando conexão...";
        try
        {
            await SimulationRepository.EnsureSchemaAsync();
            int count = await SimulationRepository.CountSimulationsAsync();
            _dbAvailable = true;
            lblDbStatus.Text = $"Banco: conectado ({DbConfig.Database}) — {count} simulação(ões) já registrada(s). " +
                               "A simulação atual é registrada quando o primeiro passo for calculado.";
        }
        catch (Exception ex)
        {
            _dbAvailable = false;
            lblDbStatus.Text = DbConfig.Password == "SUA_SENHA_AQUI"
                ? "Banco: configure usuário e senha em Data/DbConfig.cs (as simulações NÃO estão sendo registradas)"
                : $"Banco: indisponível — {Shorten(ex.Message)} (as simulações NÃO estão sendo registradas)";
        }
    }

    /// <summary>
    /// Garante que a simulação atual já tem uma linha no banco. É chamado logo ANTES do primeiro
    /// passo ser calculado (manual ou automático), e não ao criar/zerar a grade — assim a linha
    /// gravada reflete a configuração final (materiais, lado, temperaturas) que você realmente
    /// decidiu rodar, e não um estado intermediário de quem ainda estava configurando.
    /// </summary>
    private async Task EnsureSimulationRegisteredAsync(HeatGrid grid)
    {
        if (_dbInitTask != null) await _dbInitTask; // espera a verificação inicial do banco terminar
        if (!_dbAvailable || _currentSimulationId.HasValue) return;

        int? newId = null;
        bool ok = await TryDbAsync(async () =>
        {
            newId = await SimulationRepository.InsertSimulationAsync(grid);
        });

        if (ok && newId.HasValue)
        {
            _currentSimulationId = newId;
            lblDbStatus.Text = $"Banco: simulação #{newId} registrada, em andamento...";
        }
    }

    /// <summary>Atualiza o progresso da simulação atual no banco (passos, tempo, faixa de temperatura).</summary>
    private async Task<bool> LogProgressAsync(HeatGrid grid, bool reachedEquilibrium)
    {
        if (!_dbAvailable || !_currentSimulationId.HasValue) return false;
        int simulationId = _currentSimulationId.Value;
        return await TryDbAsync(() => SimulationRepository.UpdateSimulationProgressAsync(simulationId, grid, reachedEquilibrium));
    }

    /// <summary>Verifica se a grade atingiu o equilíbrio térmico e, se sim, registra isso no banco (uma única vez por simulação).</summary>
    /// <returns>true se a grade está em equilíbrio.</returns>
    private async Task<bool> CheckEquilibriumAndLogAsync(HeatGrid grid)
    {
        bool atEquilibrium = grid.IsAtEquilibrium;

        if (atEquilibrium && !_equilibriumLogged && _currentSimulationId.HasValue)
        {
            _equilibriumLogged = true;
            if (await LogProgressAsync(grid, true))
                lblDbStatus.Text = $"Banco: simulação #{_currentSimulationId} concluída (equilíbrio registrado)";
        }

        return atEquilibrium;
    }

    /// <summary>
    /// Executa uma operação de banco protegida: se falhar, desliga <see cref="_dbAvailable"/> (não
    /// tenta mais pelo resto da execução) e mostra o erro no painel — sem nunca derrubar o simulador.
    /// </summary>
    private async Task<bool> TryDbAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            return true;
        }
        catch (Exception ex)
        {
            _dbAvailable = false;
            lblDbStatus.Text = $"Banco: erro — {Shorten(ex.Message)} (as simulações NÃO estão mais sendo registradas)";
            return false;
        }
    }
}
