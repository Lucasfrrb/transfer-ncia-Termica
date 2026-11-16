using System.Threading.Tasks;
using System.Windows.Forms;

namespace HeatTransferSim.Controllers;

// Loop da simulação (assíncrono): passo único, iniciar/pausar, zerar.
public partial class Form1
{
    private async Task SingleStepAsync()
    {
        if (_grid == null) CreateGrid();
        var grid = _grid;
        if (grid == null || _isSteppingAsync || _isRunning) return;

        if (grid.IsAtEquilibrium)
        {
            lblStatus.Text = "A grade já está em equilíbrio térmico — altere a temperatura (ou o material) de algum bloco, ou clique em \"Zerar temperaturas\".";
            return;
        }

        _isSteppingAsync = true;
        _isSingleStepping = true;
        RefreshEnabledState();
        try
        {
            await EnsureSimulationRegisteredAsync(grid);
            await grid.StepAsync();
            _gridCtrl.RefreshGrid();
            UpdateInfo();

            bool atEquilibrium = await CheckEquilibriumAndLogAsync(grid);
            if (!atEquilibrium) await LogProgressAsync(grid, false);
        }
        finally
        {
            _isSteppingAsync = false;
            _isSingleStepping = false;
            RefreshEnabledState();
        }
    }

    private async void Timer_Tick(object? s, EventArgs e)
    {
        var grid = _grid;
        if (!_isRunning || grid == null || _isSteppingAsync) return; // nunca sobrepõe cálculos se um quadro atrasar

        _isSteppingAsync = true;
        try
        {
            await EnsureSimulationRegisteredAsync(grid);

            // N passos numa única tarefa em segundo plano; a interface só é acordada no final.
            await grid.StepManyAsync(StepsPerFrame);

            _gridCtrl.RefreshGrid();
            UpdateInfo();

            if (await CheckEquilibriumAndLogAsync(grid))
            {
                // Chegou ao equilíbrio: não há mais o que simular — pausa sozinha.
                StopLoop();
                UpdateInfo();
            }
            else if (++_ticksSinceLastDbUpdate >= 30) // ~1 s: não martela o banco a cada quadro
            {
                _ticksSinceLastDbUpdate = 0;
                await LogProgressAsync(grid, false);
            }
        }
        catch (Exception ex)
        {
            StopLoop();
            lblStatus.Text = $"Erro na simulação: {Shorten(ex.Message)}";
        }
        finally
        {
            _isSteppingAsync = false;
        }
    }

    /// <summary>Para o loop automático e libera os controles de edição.</summary>
    private void StopLoop()
    {
        _timer.Stop();
        _isRunning = false;
        btnPlay.Text = "Iniciar";
        RefreshEnabledState();
    }

    private async Task TogglePlayAsync()
    {
        if (_isRunning)
        {
            _timer.Stop();
            _isRunning = false;
            btnPlay.Text = "Iniciar";
            await WaitForStepToFinishAsync(); // deixa um cálculo em andamento terminar antes de liberar os controles
            if (!_isRunning) RefreshEnabledState();
            var running = _grid;
            if (running != null)
            {
                UpdateInfo();
                await LogProgressAsync(running, false);
            }
            return;
        }

        if (_grid == null) CreateGrid();
        var grid = _grid;
        if (grid == null) return;

        if (grid.IsAtEquilibrium)
        {
            lblStatus.Text = "A grade já está em equilíbrio térmico — altere a temperatura (ou o material) de algum bloco, ou clique em \"Zerar temperaturas\".";
            return;
        }

        _isRunning = true;
        RefreshEnabledState();
        _timer.Start();
        btnPlay.Text = "Pausa";
        UpdateInfo();
    }

    private async Task ResetTemperaturesAsync()
    {
        var grid = _grid;
        if (grid == null) return;

        _timer.Stop();
        _isRunning = false;
        btnPlay.Text = "Iniciar";
        await WaitForStepToFinishAsync();
        await LogProgressAsync(grid, false);   // grava o estado final da simulação que está sendo interrompida
        await WaitForStepToFinishAsync();      // (se alguém clicou em "Passo" durante o await acima)

        RefreshEnabledState();
        grid.ResetTemperatures();
        _currentSimulationId = null;           // zerar = uma simulação nova, registrada no banco no próximo passo
        _equilibriumLogged = false;
        _gridCtrl.RefreshGrid();
        UpdateSelectionUI();
        UpdateInfo(true);
    }

    private async Task WaitForStepToFinishAsync()
    {
        while (_isSteppingAsync) await Task.Delay(10);
    }
}
