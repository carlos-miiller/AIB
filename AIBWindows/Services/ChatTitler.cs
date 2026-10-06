using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIB.Services.Ai;
using AIB.Services.Memory;
using OpenAI.Chat;

namespace AIB.Services;

/// <summary>
/// Dá nome a uma conversa a partir do que ela é, e não da primeira coisa que o usuário
/// digitou.
/// <para>
/// O título saía da primeira mensagem, cortada em 40 caracteres. "me ajuda com uma coisa
/// aqui..." é um título ruim para uma conversa que virou uma investigação de três horas num
/// bug de layout.
/// </para>
/// <para>
/// Não é ferramenta que o modelo chama. Uma ferramenta entraria no schema de TODO turno, e um
/// modelo de 9B ou esquece que ela existe ou passa a chamá-la sem parar; além disso a chamada
/// apareceria na cadeia de ações e no registro como se fosse trabalho de verdade. Aqui é uma
/// chamada fora de banda, igual à do <see cref="Compactor"/>: lista de ferramentas VAZIA,
/// temperatura zero, sem raciocínio.
/// </para>
/// <para>
/// O momento é a parte importante e é contraintuitivo. Uma completion fora de banda usa um
/// prefixo diferente e derruba o cache de prefixo da conversa, que tem um slot só. Por isso o
/// título é feito CEDO, depois do primeiro turno: ali o histórico é o prompt de sistema mais
/// uma pergunta e uma resposta, e reconstruir isso não custa quase nada. Esperar a conversa
/// "amadurecer" — o instinto — é justamente o que sairia caro.
/// </para>
/// </summary>
public sealed class ChatTitler
{
    /// <summary>
    /// Teto de tokens da resposta. Um título de seis palavras não passa de 15 tokens; 24 dá
    /// folga sem deixar espaço para o modelo escrever um parágrafo.
    /// </summary>
    public const int MaxTitleTokens = 24;

    /// <summary>Limite de caracteres do título já limpo. Acima disso não cabe no cabeçalho.</summary>
    public const int MaxCaracteres = 48;

    /// <summary>
    /// Lidas a cada título, com a janela e o keep-alive em vigor. Eram um campo estático com os
    /// 32.768 do padrão do record: com outra janela na tela, cada título fazia o Ollama
    /// descarregar e recarregar o modelo — e o turno seguinte recarregava de novo.
    /// </summary>
    private static ChatRequestOptions Options => ChatRequestOptions.DeServico(MaxTitleTokens);

    private const string Prompt =
        """
        Você nomeia conversas entre um usuário e um agente de IA no Windows.

        Responda APENAS com o título, em Português (Brasil).

        Regras:
        - No máximo 6 palavras.
        - Nomeie o ASSUNTO, não a forma: "Bug de largura no balão da IA", e não "Pedido de
          ajuda" ou "Conversa sobre programação".
        - Sem aspas, sem ponto final, sem prefixo, sem explicação.
        - Se a conversa não tiver assunto claro, responda exatamente: Conversa
        """;

    private readonly IChatProvider _provider;

    public ChatTitler(IChatProvider provider) =>
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    /// <summary>
    /// Título a partir do material dado — o primeiro turno, ou o resumo de um capítulo.
    /// Devolve <c>null</c> quando não dá para titular; quem chama mantém o que já tinha.
    /// </summary>
    public async Task<string?> TitularAsync(string material, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(material)) return null;

        var mensagens = new List<ChatMessage>
        {
            ChatMessage.CreateSystemMessage(Prompt),
            ChatMessage.CreateUserMessage(material)
        };

        var resultado = await _provider
            .CompleteAsync(mensagens, Array.Empty<ChatTool>(), Options, ct)
            .ConfigureAwait(false);

        return Limpar(resultado.Text);
    }

    /// <summary>
    /// Tira do texto bruto o que o modelo costuma acrescentar por conta própria.
    /// <para>
    /// Função pura e separada porque é ela que erra: o modelo devolve aspas, ponto final,
    /// "Título:" na frente, ou um parágrafo inteiro quando ignora o limite. Testar isso com o
    /// Ollama na frente seria testar o modelo; aqui se testa a limpeza.
    /// </para>
    /// </summary>
    public static string? Limpar(string? bruto)
    {
        string texto = ThinkBlockStripper.Strip(bruto).Trim();
        if (texto.Length == 0) return null;

        // Só a primeira linha: o modelo às vezes explica a escolha logo abaixo do título.
        texto = texto.Split('\n')[0].Trim();

        foreach (var prefixo in new[] { "Título:", "Titulo:", "Title:" })
        {
            if (texto.StartsWith(prefixo, StringComparison.OrdinalIgnoreCase))
                texto = texto.Substring(prefixo.Length).Trim();
        }

        texto = texto.Trim('"', '\'', '«', '»', '“', '”', '*', '#', ' ').Trim();
        texto = texto.TrimEnd('.', ':', ';').Trim();

        // Espaços internos repetidos: acontece quando o modelo quebra linha no meio.
        texto = string.Join(" ", texto.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (texto.Length == 0) return null;

        // Longo demais não é título, é resposta. Melhor manter a heurística antiga do que
        // pendurar meio parágrafo no cabeçalho.
        if (texto.Length > MaxCaracteres) return null;

        return texto;
    }

    /// <summary>
    /// Junta pergunta e resposta no material que vai para o modelo, com as duas partes
    /// aparadas: o título sai da INTENÇÃO, e para isso o começo de cada uma basta.
    /// </summary>
    public static string Material(string pergunta, string resposta, int limitePorLado = 600)
    {
        string Aparar(string t) =>
            string.IsNullOrEmpty(t) || t.Length <= limitePorLado ? t ?? "" : t.Substring(0, limitePorLado);

        return $"Usuário: {Aparar(pergunta)}\n\nAgente: {Aparar(ThinkBlockStripper.Strip(resposta))}".Trim();
    }
}
