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
    /// </summary>
    private static readonly ChatRequestOptions Options = new(Temperature: 0.0f);

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
