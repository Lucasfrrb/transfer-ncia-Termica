-- Esquema do banco MySQL do Simulador de Transferência de Calor.
--
-- Você NÃO precisa rodar este arquivo manualmente: o programa cria essas
-- mesmas tabelas sozinho, ao abrir a janela (veja EnsureSchemaAsync em
-- Data/SimulationRepository.cs), desde que o BANCO em si já exista.
--
-- O que você precisa fazer manualmente, uma vez só:
--   1) Ter um servidor MySQL rodando (local: XAMPP/WAMP/MySQL Server/Workbench).
--   2) Criar um banco (schema) VAZIO com o nome abaixo (ajuste em Data/DbConfig.cs
--      se quiser outro nome):
--
--         CREATE DATABASE IF NOT EXISTS transferencia_termica
--             DEFAULT CHARACTER SET utf8mb4;
--
--   3) Editar Data/DbConfig.cs com host/porta/usuário/senha do seu MySQL.
--
-- Este arquivo existe só para você conferir/estudar a estrutura das tabelas;
-- ele é mantido em sincronia manual com o que EnsureSchemaAsync cria.
--
-- Atualização: se o seu banco foi criado pela versão anterior (grade quadrada,
-- coluna GridSize), o programa converte sozinho: GridSize vira GridRows e GridCols.

CREATE DATABASE IF NOT EXISTS transferencia_termica
    DEFAULT CHARACTER SET utf8mb4;

USE transferencia_termica;

-- Uma linha por simulação (cada vez que a grade é criada/recriada OU zerada).
CREATE TABLE IF NOT EXISTS simulations (
    Id                   INT AUTO_INCREMENT PRIMARY KEY,
    StartedAt            DATETIME NOT NULL,      -- quando a simulação começou (UTC)
    FinishedAt           DATETIME NULL,          -- quando atingiu o equilíbrio (UTC); NULL = ainda rodando/não concluída
    GridRows             INT NOT NULL,           -- número de linhas da grade
    GridCols             INT NOT NULL,           -- número de colunas da grade
    CubeSideMeters       DOUBLE NOT NULL,        -- lado do cubo (m)
    MaterialName         VARCHAR(60) NOT NULL,   -- MATERIAL GERAL (vale para os blocos sem material próprio)
    ThermalConductivity  DOUBLE NOT NULL,        -- k (W/m·K) do material geral, no momento do registro
    SpecificHeat         DOUBLE NOT NULL,        -- c (J/kg·K)
    Density              DOUBLE NOT NULL,        -- rho (kg/m³)
    SafeTimeStepSeconds  DOUBLE NOT NULL,        -- dt calculado automaticamente para estabilidade numérica
    StepsComputed        BIGINT NOT NULL DEFAULT 0,   -- quantos passos (dt) já foram calculados
    SimulatedTimeSeconds DOUBLE NOT NULL DEFAULT 0,   -- tempo simulado acumulado (s)
    MinTemperature       DOUBLE NULL,            -- menor temperatura na grade (última leitura registrada)
    MaxTemperature       DOUBLE NULL,            -- maior temperatura na grade (última leitura registrada)
    ReachedEquilibrium   TINYINT(1) NOT NULL DEFAULT 0  -- 1 = bloco mais quente e mais frio diferem < 0,001 °C
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Uma linha por bloco que recebeu temperatura inicial customizada numa simulação.
CREATE TABLE IF NOT EXISTS simulation_initial_temperatures (
    Id            INT AUTO_INCREMENT PRIMARY KEY,
    SimulationId  INT NOT NULL,
    RowIndex      INT NOT NULL,     -- linha do bloco na grade (0 = primeira linha)
    ColIndex      INT NOT NULL,     -- coluna do bloco na grade (0 = primeira coluna)
    Temperature   DOUBLE NOT NULL,  -- temperatura inicial aplicada a esse bloco (°C)
    CONSTRAINT FK_SimTemps_Simulation FOREIGN KEY (SimulationId)
        REFERENCES simulations (Id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Uma linha por bloco que recebeu um material PRÓPRIO (diferente do material geral).
-- Os blocos que NÃO aparecem aqui usam o material geral da tabela simulations.
CREATE TABLE IF NOT EXISTS simulation_block_materials (
    Id                   INT AUTO_INCREMENT PRIMARY KEY,
    SimulationId         INT NOT NULL,
    RowIndex             INT NOT NULL,
    ColIndex             INT NOT NULL,
    MaterialName         VARCHAR(60) NOT NULL,
    ThermalConductivity  DOUBLE NOT NULL,   -- k (W/m·K) do material desse bloco
    SpecificHeat         DOUBLE NOT NULL,   -- c (J/kg·K)
    Density              DOUBLE NOT NULL,   -- rho (kg/m³)
    CONSTRAINT FK_SimMaterials_Simulation FOREIGN KEY (SimulationId)
        REFERENCES simulations (Id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ---------------------------------------------------------------------------
-- Consultas úteis para conferir o que foi registrado
-- (troque o 1 pelo Id da simulação que você quer ver):
-- ---------------------------------------------------------------------------

-- Últimas 20 simulações:
-- SELECT * FROM simulations ORDER BY StartedAt DESC LIMIT 20;

-- Resumo legível: tamanho da grade, material geral, tempo simulado, se chegou ao equilíbrio:
-- SELECT Id, StartedAt, CONCAT(GridRows, 'x', GridCols) AS Grade, MaterialName AS MaterialGeral,
--        StepsComputed AS Passos, ROUND(SimulatedTimeSeconds / 60, 1) AS Minutos,
--        MinTemperature, MaxTemperature, ReachedEquilibrium
-- FROM simulations ORDER BY Id DESC;

-- Temperaturas iniciais de uma simulação:
-- SELECT RowIndex, ColIndex, Temperature FROM simulation_initial_temperatures WHERE SimulationId = 1;

-- Blocos com material próprio de uma simulação (ex.: a fila de isolante):
-- SELECT RowIndex, ColIndex, MaterialName, ThermalConductivity
-- FROM simulation_block_materials WHERE SimulationId = 1 ORDER BY RowIndex, ColIndex;

-- Simulações que usaram algum bloco com material próprio, e quantos:
-- SELECT s.Id, s.MaterialName AS MaterialGeral, COUNT(*) AS BlocosComMaterialProprio
-- FROM simulations s JOIN simulation_block_materials m ON m.SimulationId = s.Id
-- GROUP BY s.Id, s.MaterialName;

-- Quantas simulações já chegaram ao equilíbrio térmico:
-- SELECT COUNT(*) FROM simulations WHERE ReachedEquilibrium = 1;
