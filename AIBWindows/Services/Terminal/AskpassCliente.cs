using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIB.Services.Terminal;

/// <summary>
/// O lado askpass: o AIB.exe chamado pelo ssh (ou git) com o prompt como argumento.
/// <para>
/// Não sobe o app. Leva o prompt ao <see cref="AskpassServidor"/> da instância que rodou o
/// comando, espera a resposta do usuário e a escreve na saída padrão — é daí que o ssh lê.
/// Sai com 1 quando o usuário cancela ou o pedido não é aceito; o ssh trata como senha
/// recusada.
/// </para>
/// </summary>
public static class AskpassCliente
{
    /// <summary>
    /// Se este processo foi chamado como askpass. As duas variáveis só existem no ambiente dos
    /// comandos que o shell da AIB roda, e o askpass sempre recebe o prompt como argumento.
    /// </summary>
    public static bool EstaNoModoAskpass(string[] args) =>
        args.Length > 0
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AskpassServidor.VarPipe))
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AskpassServidor.VarToken));

    public static int Rodar(string[] args)
    {
        string pipe = Environment.GetEnvironmentVariable(AskpassServidor.VarPipe) ?? "";
        string token = Environment.GetEnvironmentVariable(AskpassServidor.VarToken) ?? "";
        string prompt = string.Join(" ", args);

        string? resposta;
        try
        {
            // Task.Run: isto roda na thread do WPF, e esperar ali uma continuação que quer
            // voltar para ela é deadlock — o askpass ficava parado para sempre.
            resposta = Task.Run(() => PedirAsync(pipe, token, prompt)).GetAwaiter().GetResult();
        }
        catch
        {
            resposta = null;
        }

        if (resposta is null) return 1;

        using var saida = Console.OpenStandardOutput();
        byte[] bytes = new UTF8Encoding(false).GetBytes(resposta + "\n");
        saida.Write(bytes, 0, bytes.Length);
        saida.Flush();
        return 0;
    }

    /// <summary>
    /// Uma ida e volta pelo pipe. Sem prazo na resposta: do outro lado há uma pessoa digitando.
    /// </summary>
    public static async Task<string?> PedirAsync(string nomeDoPipe, string token, string prompt,
                                                 CancellationToken cancelar = default)
    {
        await using var pipe = new NamedPipeClientStream(".", nomeDoPipe, PipeDirection.InOut,
                                                         PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, cancelar);

        byte[] pedido = new UTF8Encoding(false).GetBytes(
            JsonSerializer.Serialize(new { Token = token, Prompt = prompt }) + "\n");
        await pipe.WriteAsync(pedido, cancelar);
        await pipe.FlushAsync(cancelar);

        using var leitor = new StreamReader(pipe, new UTF8Encoding(false));
        string? linha = await leitor.ReadLineAsync(cancelar);
        if (linha is null) return null;

        using var doc = JsonDocument.Parse(linha);
        var raiz = doc.RootElement;
        if (!raiz.TryGetProperty("Ok", out var ok) || !ok.GetBoolean()) return null;
        return raiz.TryGetProperty("Texto", out var texto) ? texto.GetString() : null;
    }
}
