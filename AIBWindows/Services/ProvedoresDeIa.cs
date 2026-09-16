using System;
using System.Collections.Generic;
using System.Linq;

namespace AIB.Services;

/// <summary>
/// Os provedores de IA que a AIB fala, e o que é próprio de cada um.
/// <para>
/// Existe porque a configuração fingia que qualquer provedor era "o Ollama com outra URL". A tela
/// oferecia OpenAI, Anthropic e LM Studio com os mesmos campos — keep-alive, lista de modelos do
/// <c>/api/tags</c>, "chave não necessária para Ollama" — e nenhum deles fazia sentido fora do
/// Ollama. O OpenRouter, que é o que de fato se usa na nuvem, nem estava na lista.
/// </para>
/// <para>
/// Dois provedores, de propósito: o Ollama, local, e o OpenRouter, que dá acesso aos modelos de
/// nuvem por uma API só. Acrescentar um terceiro é acrescentar aqui, na fábrica e na tela.
/// </para>
/// </summary>
public static class ProvedoresDeIa
{
    public const string Ollama = "Ollama";
    public const string OpenRouter = "OpenRouter";

    public static IReadOnlyList<string> Todos { get; } = new[] { Ollama, OpenRouter };

    public const string UrlDoOllama = "http://127.0.0.1:11434";
    public const string UrlDoOpenRouter = "https://openrouter.ai/api/v1";

    /// <summary>Onde a chave do provedor mora no cofre. Nulo para quem não usa chave.</summary>
    public static string? SistemaDaChave(string provedor) =>
        provedor == OpenRouter ? "openrouter" : null;

    public const string NomeDaChave = "ApiKey";

    /// <summary>
    /// O provedor gravado, no nome que a AIB conhece. Gravações antigas trazem "OpenAI",
    /// "Anthropic" ou "LmStudio": uma URL do OpenRouter as leva ao OpenRouter; o resto volta ao
    /// Ollama, que é o único que funciona sem configuração a mais.
    /// </summary>
    public static string Normalizar(string? provedor, string? url)
    {
        string p = (provedor ?? "").Trim();

        if (string.Equals(p, Ollama, StringComparison.OrdinalIgnoreCase)) return Ollama;
        if (string.Equals(p, OpenRouter, StringComparison.OrdinalIgnoreCase)) return OpenRouter;
        if (p.Length == 0) return "";

        return (url ?? "").Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase)
            ? OpenRouter
            : Ollama;
    }

    /// <summary>O perfil de fábrica de um provedor.</summary>
    public static PerfilDeProvedor PerfilPadrao(string provedor) => provedor == OpenRouter
        ? new PerfilDeProvedor
        {
            Url = UrlDoOpenRouter,
            Modelo = "",
            KeepAlive = "",
            JanelaDeContexto = PerfilDeProvedor.JanelaPadrao,
            Raciocinio = PerfilDeProvedor.RaciocinioDesligado
        }
        : new PerfilDeProvedor
        {
            Url = UrlDoOllama,
            Modelo = "qwen2.5:7b",
            KeepAlive = "-1",
            JanelaDeContexto = PerfilDeProvedor.JanelaPadrao,
            Raciocinio = PerfilDeProvedor.RaciocinioDesligado
        };

    /// <summary>Os valores de raciocínio que fazem sentido em cada provedor, na ordem da tela.</summary>
    public static IReadOnlyList<(string Valor, string Rotulo)> OpcoesDeRaciocinio(string provedor) =>
        provedor == OpenRouter
            ? new[]
            {
                (PerfilDeProvedor.RaciocinioDesligado, "Desligado"),
                ("low", "Baixo"),
                ("medium", "Médio"),
                ("high", "Alto"),
                (PerfilDeProvedor.RaciocinioDoModelo, "Padrão do modelo")
            }
            : new[]
            {
                (PerfilDeProvedor.RaciocinioDesligado, "Desligado"),
                (PerfilDeProvedor.RaciocinioDoModelo, "Ligado (padrão do modelo)")
            };
}

/// <summary>
/// A configuração de UM provedor. Cada provedor guarda a sua: voltar ao Ollama depois de testar
/// o OpenRouter traz de volta o modelo, a janela e o keep-alive que ele tinha.
/// <para>
/// A chave NÃO está aqui. Este objeto vai para o arquivo de configurações; a chave vai para o
/// cofre, por provedor (<see cref="ProvedoresDeIa.SistemaDaChave"/>).
/// </para>
/// </summary>
public sealed class PerfilDeProvedor
{
    public const int JanelaPadrao = 32768;
    public const int JanelaMinima = 8192;

    /// <summary>
    /// Teto do que a AIB aceita pedir. Acima disso a conta dos orçamentos por nível e o custo de
    /// cada turno deixam de ser razoáveis nesta máquina e na fatura; o modelo pode aguentar mais.
    /// </summary>
    public const int JanelaMaxima = 262144;

    public const string RaciocinioDesligado = "off";
    public const string RaciocinioDoModelo = "model";

    public string Url { get; set; } = "";
    public string Modelo { get; set; } = "";

    /// <summary>Só Ollama: "1m", "5m", "30m" ou "-1" (sempre carregado).</summary>
    public string KeepAlive { get; set; } = "";

    /// <summary>
    /// A janela que a AIB usa. No Ollama é o <c>num_ctx</c> pedido; nos dois provedores é a base
    /// dos orçamentos de histórico por nível.
    /// </summary>
    public int JanelaDeContexto { get; set; } = JanelaPadrao;

    /// <summary>
    /// <see cref="RaciocinioDesligado"/>, <see cref="RaciocinioDoModelo"/> ou, no OpenRouter,
    /// um esforço: "low", "medium", "high".
    /// </summary>
    public string Raciocinio { get; set; } = RaciocinioDesligado;

    public PerfilDeProvedor Clone() => (PerfilDeProvedor)MemberwiseClone();

    /// <summary>Números e valores dentro do que o provedor aceita.</summary>
    public PerfilDeProvedor Sanear(string provedor)
    {
        JanelaDeContexto = Math.Clamp(JanelaDeContexto <= 0 ? JanelaPadrao : JanelaDeContexto, JanelaMinima, JanelaMaxima);

        if (!ProvedoresDeIa.OpcoesDeRaciocinio(provedor).Any(o => o.Valor == Raciocinio))
            Raciocinio = RaciocinioDesligado;

        if (provedor == ProvedoresDeIa.OpenRouter)
        {
            // O endereço do OpenRouter não é escolha: é onde a API está.
            Url = ProvedoresDeIa.UrlDoOpenRouter;
            KeepAlive = "";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(Url)) Url = ProvedoresDeIa.UrlDoOllama;
            if (KeepAlive is not ("1m" or "5m" or "30m" or "-1")) KeepAlive = "-1";
        }

        return this;
    }
}

/// <summary>
/// Quanto as ferramentas trazem de uma vez, por provedor.
/// <para>
/// Os tetos do Ollama foram medidos pela lentidão local: o prefill anda a ~30 tok/s, e cada mil
/// tokens a mais eram meio minuto antes da primeira palavra. No OpenRouter o mesmo prompt chega
/// em segundos e custa frações de centavo — ali o teto pequeno só obriga o modelo a pedir o
/// resto em mais voltas, e cada volta reenvia o prompt inteiro, o que sai MAIS caro.
/// </para>
/// </summary>
/// <param name="LinhasDeLeitura">Linhas por leitura de arquivo, quando ninguém pede faixa.</param>
/// <param name="ItensDaPasta">Entradas listadas de uma pasta.</param>
/// <param name="EmailPorMensagem">Caracteres do corpo de cada mensagem em <c>mail_read</c>.</param>
/// <param name="EmailPorLeitura">Caracteres da leitura inteira em <c>mail_read</c>.</param>
/// <param name="LoteDaTriagem">Mensagens numa chamada de triagem.</param>
/// <param name="AlvoDepoisDeCompactar">
/// Fração da cota em que a conversa viva fica depois de compactar. Cada compactação reescreve o
/// começo do prompt e perde o cache do provedor; no OpenRouter isso é dinheiro, então ela libera
/// mais espaço de uma vez e volta menos vezes.
/// </param>
public sealed record LimitesDoProvedor(
    int LinhasDeLeitura, int ItensDaPasta, int EmailPorMensagem, int EmailPorLeitura, int LoteDaTriagem,
    double AlvoDepoisDeCompactar)
{
    public static readonly LimitesDoProvedor Local = new(
        Tools.ReadFileTool.LinhasPadrao, Tools.ReadFileTool.TetoDaPasta,
        Tools.LerEmailTool.TetoPorMensagem, Tools.LerEmailTool.TetoDaLeitura,
        Mail.MailDigestService.TetoDoLote, 0.5);

    public static readonly LimitesDoProvedor Nuvem = new(1500, 300, 12000, 32000, 60, 0.3);

    public static LimitesDoProvedor Para(string? provedor) =>
        provedor == ProvedoresDeIa.OpenRouter ? Nuvem : Local;

    /// <summary>
    /// Os do provedor da conversa. Configurado pelo <see cref="SettingsService"/> ao carregar e
    /// ao salvar, como <see cref="Ai.ChatRequestOptions.JanelaAtual"/>.
    /// </summary>
    public static LimitesDoProvedor Atual { get; set; } = Local;
}