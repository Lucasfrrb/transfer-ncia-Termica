using HeatTransferSim.Controllers;

namespace HeatTransferSim;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    /// <remarks>
    /// Main continua síncrono e [STAThread] (o WinForms precisa da thread em modo STA). A conexão
    /// com o banco MySQL NÃO é feita aqui: o formulário a verifica em segundo plano logo depois
    /// de aparecer (Form1.OnShown), então a janela abre na hora mesmo que o MySQL esteja
    /// desligado ou lento para responder.
    /// </remarks>
    [STAThread]
    static void Main()
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        Application.Run(new Form1());
    }
}
