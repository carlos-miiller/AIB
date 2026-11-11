using System.Collections.Generic;

namespace AIB.Services.Ai;

/// <summary>Uma fonte da pesquisa na internet: de onde veio o que a resposta diz.</summary>
/// <param name="Trecho">O pedaço da página que o provedor devolveu, quando devolve. Pode ser vazio.</param>
public sealed record FonteDaPesquisa(string Titulo, string Url, string Trecho);

/// <summary>A resposta de uma pesquisa feita pelo provedor, com as fontes e o que custou.</summary>
/// <param name="CustoUsd">O que a busca custou, quando o provedor relata (OpenRouter).</param>
public sealed record ResultadoDaPesquisa(string Texto, IReadOnlyList<FonteDaPesquisa> Fontes, decimal? CustoUsd = null);

/// <summary>
/// O que é igual na pesquisa de todo provedor: o pedido ao modelo e os tetos.
/// <para>
/// A busca é do PROVEDOR (o plugin <c>web</c> do OpenRouter, o <c>google_search</c> do Gemini),
/// mas roda numa chamada à parte, pedida pela ferramenta <c>web_search</c>. Ligada direto na
/// conversa, o texto das páginas entraria no histórico sem passar pelo <c>ToolRegistry</c>: sem a
/// marca de conteúdo de terceiros, sem a redação de segredos e sem aparecer na tela.
/// </para>
/// </summary>
public static class Pesquisa
{
    /// <summary>
    /// O pedido de sistema da chamada de busca. Curto e preso às fontes: a resposta volta como
    /// resultado de ferramenta e é paga de novo em toda volta seguinte do turno.
    /// </summary>
    public const string Instrucao =
        "Pesquise na internet e responda em português, curto (até 12 linhas), só com o que as "
        + "fontes dizem. Dê datas e números exatos quando houver. Se as fontes não respondem, diga isso.";

    /// <summary>Quantas páginas a busca traz. O plugin do OpenRouter cobra por resultado acima de 10.</summary>
    public const int Resultados = 5;

    /// <summary>Teto de cada trecho de fonte no resultado: a resposta já resume, o trecho só confere.</summary>
    public const int TetoDoTrecho = 300;
}
