using System;
using System.Text.RegularExpressions;

namespace AIB.Services.Mail;

/// <summary>
/// O texto original de um e-mail dentro da conversa — marcado para nunca chegar ao disco.
/// <para>
/// A conversa com a IA pode ler o corpo de um e-mail sob demanda (<c>mail_read</c>), e ele fica
/// no contexto vivo enquanto a conversa está aberta. Mas tudo o que passa pela conversa vai
/// para lugares que ficam: o <c>raw.jsonl</c>, que nunca é apagado; o texto que o resumidor de
/// capítulos lê; o registro de execução, que espelha o console inteiro; e o
/// <c>chat_history.json</c>, de onde a conversa volta pelo painel. A REGRA 3 diz que o corpo
/// não toca o disco, e ela continua valendo — só deixou de valer para a memória RAM.
/// </para>
/// <para>
/// Por isso o corpo entra EMBRULHADO entre dois marcadores, e cada saída para disco passa por
/// <see cref="Redigir"/>, que troca o trecho inteiro por <see cref="Omitido"/>. Um redator só,
/// chamado nos QUATRO pontos de saída, em vez de quatro regras parecidas que um dia
/// discordariam. Eram três: o histórico do painel ficou de fora até ser notado — quem abrir um
/// quinto caminho para o disco tem de passar por aqui também.
/// </para>
/// <para>
/// Os marcadores são ASCII sem sinal de menor, maior, aspas ou barra: o serializador de JSON
/// escapa esses caracteres, e o log do provedor imprime a requisição em JSON. Um marcador
/// escapado não casaria mais, e o corpo passaria direto para o arquivo.
/// </para>
/// </summary>
public static class ConteudoDeTerceiros
{
    public const string Inicio = "[[INICIO_DO_EMAIL]]";
    public const string Fim = "[[FIM_DO_EMAIL]]";

    /// <summary>O que fica no lugar do corpo em tudo o que é gravado.</summary>
    public const string Omitido = "[corpo do e-mail omitido — não é gravado]";

    // Sem o marcador de fim, apaga até o fim do texto: um corte no meio (log truncado, resultado
    // aparado) não pode deixar a segunda metade do corpo escapar.
    private static readonly Regex Trecho = new(
        @"\[\[INICIO_DO_EMAIL\]\].*?(?:\[\[FIM_DO_EMAIL\]\]|$)",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Põe o corpo entre os marcadores.
    /// <para>
    /// Tira antes qualquer marcador que já venha DENTRO do corpo. O texto é de terceiros: quem
    /// escrevesse <c>[[FIM_DO_EMAIL]]</c> no meio da mensagem fecharia o embrulho antes da hora,
    /// e o resto do corpo seria gravado. A remoção se repete até estabilizar, porque tirar um
    /// marcador pode juntar as duas metades de outro.
    /// </para>
    /// </summary>
    public static string Embrulhar(string? corpo)
    {
        string limpo = corpo ?? "";
        string anterior;

        do
        {
            anterior = limpo;
            limpo = limpo.Replace(Inicio, "", StringComparison.Ordinal)
                         .Replace(Fim, "", StringComparison.Ordinal);
        }
        while (limpo.Length != anterior.Length);

        return Inicio + "\n" + limpo + "\n" + Fim;
    }

    /// <summary>Se há corpo de e-mail neste texto.</summary>
    public static bool Contem(string? texto) =>
        texto != null && texto.Contains(Inicio, StringComparison.Ordinal);

    /// <summary>Troca cada corpo embrulhado por <see cref="Omitido"/>. Texto sem corpo volta intacto.</summary>
    public static string Redigir(string? texto)
    {
        if (string.IsNullOrEmpty(texto) || !Contem(texto)) return texto ?? "";
        return Trecho.Replace(texto, Omitido);
    }
}
