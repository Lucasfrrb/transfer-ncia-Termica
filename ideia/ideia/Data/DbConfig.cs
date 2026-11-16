namespace HeatTransferSim.Data;

/// <summary>
/// Configuração de conexão com o banco MySQL.
/// </summary>
/// <remarks>
/// EDITE os valores abaixo com os dados do seu MySQL antes de rodar o programa
/// (host, porta, usuário, senha e o nome do banco). O banco em si (um "schema"
/// vazio chamado <see cref="Database"/>) precisa existir antes — basta criar um
/// banco vazio com esse nome (<c>CREATE DATABASE transferencia_termica;</c>); as
/// tabelas dentro dele são criadas automaticamente pelo próprio programa na
/// primeira vez que ele roda (veja <see cref="SimulationRepository.EnsureSchemaAsync"/>),
/// então você não precisa rodar o schema.sql manualmente — ele está incluído só
/// para você conferir/editar a estrutura das tabelas se quiser.
///
/// Dicas: no XAMPP o usuário padrão é "root" com senha VAZIA (Password = ""); no
/// MySQL Server/Workbench é a senha que você definiu na instalação. Evite os
/// caracteres ; e = na senha (eles quebram a string de conexão).
/// </remarks>
public static class DbConfig
{
    public static readonly string Host = "localhost";
    public static readonly int Port = 3306;
    public static readonly string Database = "transferencia_termica";
    public static readonly string User = "root";
    public static readonly string Password = "root";

    /// <summary>String de conexão montada a partir dos campos acima (timeout curto: 5 s).</summary>
    public static string ConnectionString =>
        $"Server={Host};Port={Port};Database={Database};Uid={User};Pwd={Password};" +
        "AllowPublicKeyRetrieval=True;ConnectionTimeout=5;";
}
