using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIB.Services.Ai;
using OpenAI.Chat;

namespace AIB.Services.Memory;

/// <summary>
/// Transforma um bloco de turnos num <see cref="Chapter"/>.
/// <para>
/// Usa <see cref="IChatProvider.CompleteAsync"/> e passa a lista de ferramentas VAZIA:
/// resumir não é agir. Um resumidor com ferramentas na mão eventualmente decide "verificar"
/// o que está resumindo e executa comandos no meio da compactação.
/// </para>
/// <para>
/// O resumo cobre só a narrativa. Os literais saem do <see cref="ArtifactExtractor"/>, por
/// fora do modelo, e são anexados depois — nada que importe depende do resumidor acertar.
/// </para>
/// </summary>
public sealed class Compactor
{
    /// <summary>
    /// Temperatura zero: resumo é trabalho de fidelidade, não de criatividade. A persona do
    /// personagem não participa daqui — quem resume é o sistema, não a Ayano.
    /// <para>
    /// <c>Think: false</c> saiu de uma medição, não de gosto. Resumir cinco turnos no
    /// qwen3.5:4b custava 286,6s, dos quais 226,7s eram 1.738 tokens de raciocínio para
    /// produzir 117 tokens de resumo — e o <see cref="ThinkBlockStripper"/> jogava esse
    /// raciocínio fora logo em seguida. Com ele desligado: 14,7s, e o resumo saiu melhor.
    /// Pensar não ajuda a resumir; o material já está todo na frente do modelo.
    /// </para>
    /// <para>
    /// <c>NumPredict</c> é a rede embaixo: um modelo que ignore o "máximo 120 palavras" não
    /// pode gastar a janela inteira e levar a compactação ao estouro do tempo.
    /// </para>
    /// </summary>
    private static readonly ChatRequestOptions Options =
        new(Temperature: 0.0f, Think: false, NumPredict: MaxSummaryTokens);

    /// <summary>
    /// Teto de tokens do resumo. 400 dá folga larga sobre as 120 palavras pedidas (~180
    /// tokens) sem deixar espaço para um resumo desgovernado.
    /// </summary>
    public const int MaxSummaryTokens = 400;

    private const string SummarizerPrompt =
        """
        Você resume trechos de uma conversa entre um usuário e um agente de IA no Windows.

        Escreva um parágrafo único, em Português (Brasil), na terceira pessoa e no passado,
        cobrindo: o que o usuário pediu, o que o agente fez e como terminou.

        Regras:
        - Registre o que FALHOU com o mesmo cuidado do que deu certo.
        - Não invente nada que não esteja no trecho.
        - Não copie caminhos de arquivo nem linhas de comando: eles são preservados à parte.
        - Sem listas, sem títulos, sem preâmbulo. Só o parágrafo.
        - No máximo 120 palavras.
        """;

    private const string ActPrompt =
        """
        Você recebe vários resumos consecutivos de uma mesma conversa longa entre um usuário e
        um agente de IA no Windows, em ordem cronológica.

        Escreva UM parágrafo único, em Português (Brasil), na terceira pessoa e no passado, que
        conte o arco inteiro: o que foi perseguido ao longo do trecho, o que foi conseguido e o
        que ficou em aberto.

        Regras:
        - O que ficou pendente ou falhou importa tanto quanto o que foi concluído.
        - Não invente nada que não esteja nos trechos.
        - Não copie caminhos de arquivo nem linhas de comando: eles são preservados à parte.
        - Sem listas, sem títulos, sem preâmbulo. Só o parágrafo.
        - No máximo 150 palavras.
        """;

    private readonly IChatProvider _provider;

    public Compactor(IChatProvider provider) =>
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    /// <summary>
    /// Fecha um capítulo a partir de turnos COMPLETOS.
    /// <para>
    /// Quando o resumo falha (modelo fora, rede caída, resposta vazia), devolve um capítulo
    /// só com os artefatos e uma nota no lugar do resumo. Perder a narrativa é aceitável;
    /// devolver <c>null</c> não é — o chamador já removeu, ou vai remover, os turnos do
    /// contexto vivo, e um capítulo vazio deixaria um buraco silencioso.
    /// </para>
    /// </summary>
    public async Task<Chapter> SummarizeAsync(
        int chapterIndex,
        IReadOnlyList<Turn> turns,
        CancellationToken ct)
    {
        if (turns == null || turns.Count == 0)
            throw new ArgumentException("Capítulo precisa de pelo menos um turno.", nameof(turns));

        var artefatos = turns.SelectMany(ArtifactExtractor.Extract).ToList();
        string agora = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        string resumo;
        try
        {
            var mensagens = new List<ChatMessage>
            {
                ChatMessage.CreateSystemMessage(SummarizerPrompt),
                ChatMessage.CreateUserMessage(RenderForSummary(turns))
            };

            var resultado = await _provider
                .CompleteAsync(mensagens, Array.Empty<ChatTool>(), Options, ct)
                .ConfigureAwait(false);

            // O resumidor pode ser um modelo de raciocínio: o bloco <think> vem no texto e não
            // é resumo nenhum.
            resumo = ThinkBlockStripper.Strip(resultado.Text);

            if (string.IsNullOrWhiteSpace(resumo))
                resumo = "[resumo indisponível: o modelo devolveu texto vazio]";
        }
        catch (OperationCanceledException)
        {
            // Cancelamento é do usuário ou do encerramento do app. Não vira capítulo mutilado.
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MEMORIA] Resumo do capítulo {chapterIndex} falhou: {ex.Message}");
            resumo = "[resumo indisponível: falha ao contatar o modelo]";
        }

        return new Chapter(
            chapterIndex,
            agora,
            turns[0].Index,
            turns[^1].Index,
            resumo,
            artefatos);
    }

    /// <summary>
    /// Fecha um ato a partir de capítulos já resumidos — o nível 2 da hierarquia.
    /// <para>
    /// Resume só a narrativa dos capítulos. Os artefatos NÃO são reenviados ao modelo: eles
    /// vêm do <see cref="ArtifactDigest.Condense"/>, por fora. Resumir um resumo já perde
    /// detalhe; deixar o literal passar por essa segunda perda é como o caminho de arquivo
    /// vira "um arquivo do provider".
    /// </para>
    /// </summary>
    public async Task<Act> PromoteAsync(
        int actIndex,
        IReadOnlyList<Chapter> chapters,
        CancellationToken ct)
    {
        if (chapters == null || chapters.Count == 0)
            throw new ArgumentException("Ato precisa de pelo menos um capítulo.", nameof(chapters));

        var artefatos = ArtifactDigest.Condense(chapters.SelectMany(c => c.Artifacts));
        string agora = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        string resumo;
        try
        {
            var mensagens = new List<ChatMessage>
            {
                ChatMessage.CreateSystemMessage(ActPrompt),
                ChatMessage.CreateUserMessage(RenderChaptersForSummary(chapters))
            };

            var resultado = await _provider
                .CompleteAsync(mensagens, Array.Empty<ChatTool>(), Options, ct)
                .ConfigureAwait(false);

            resumo = ThinkBlockStripper.Strip(resultado.Text);

            if (string.IsNullOrWhiteSpace(resumo))
                resumo = "[resumo indisponível: o modelo devolveu texto vazio]";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MEMORIA] Resumo do ato {actIndex} falhou: {ex.Message}");
            resumo = "[resumo indisponível: falha ao contatar o modelo]";
        }

        return new Act(
            actIndex,
            agora,
            chapters[0].Index,
            chapters[^1].Index,
            chapters[0].FirstTurn,
            chapters[^1].LastTurn,
            resumo,
            artefatos);
    }

    /// <summary>Capítulos em texto plano, pelo mesmo motivo do <see cref="RenderForSummary"/>.</summary>
    public static string RenderChaptersForSummary(IReadOnlyList<Chapter> chapters)
    {
        var texto = new StringBuilder();

        foreach (var capitulo in chapters)
        {
            texto.Append("TRECHO ").Append(capitulo.Index + 1).Append(": ");
            texto.Append(capitulo.Summary.Trim()).Append('\n');
        }

        return texto.ToString();
    }

    /// <summary>
    /// Serializa os turnos para o resumidor ler. Formato plano de propósito: papéis marcados
    /// como texto, e não como mensagens de verdade, para o modelo tratar o trecho como
    /// MATERIAL a resumir e não como uma conversa da qual ele é o próximo turno.
    /// </summary>
    public static string RenderForSummary(IReadOnlyList<Turn> turns)
    {
        var texto = new StringBuilder();

        foreach (var turno in turns)
        {
            string pedido = turno.UserText.Trim();
            if (pedido.Length > 0)
                texto.Append("USUÁRIO: ").Append(pedido).Append('\n');

            foreach (var msg in turno.Messages)
            {
                if (msg is AssistantChatMessage a && a.ToolCalls is { Count: > 0 })
                {
                    foreach (var call in a.ToolCalls)
                        texto.Append("AGENTE CHAMOU: ").Append(call.FunctionName).Append('\n');
                }
                else if (msg is ToolChatMessage t)
                {
                    // Resultado truncado: o conteúdo inteiro de um arquivo lido não ajuda a
                    // resumir e é justamente o que estoura o contexto do resumidor.
                    texto.Append("RESULTADO: ").Append(Truncate(Turn.TextOf(t), 300)).Append('\n');
                }
            }

            string resposta = turno.AssistantText.Trim();
            if (resposta.Length > 0)
                texto.Append("AGENTE: ").Append(Truncate(resposta, 1200)).Append('\n');

            texto.Append('\n');
        }

        return texto.ToString();
    }

    private static string Truncate(string texto, int limite)
    {
        if (string.IsNullOrEmpty(texto) || texto.Length <= limite) return texto ?? "";
        return texto[..limite] + "…[truncado]";
    }
}
