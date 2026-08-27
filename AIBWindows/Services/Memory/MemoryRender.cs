using System;

namespace AIB.Services.Memory;

/// <summary>
/// Encolhe um bloco já renderizado de capítulo ou ato para caber numa cota de tokens.
/// <para>
/// Só o RESUMO encolhe. Cabeçalho e artefatos ficam inteiros, e o motivo é o mesmo que
/// justifica os artefatos existirem: o literal é a parte que não pode ser perdida, e ele é a
/// última coisa do bloco. Aparar o texto pelo fim, como se fosse prosa comum, comeria os
/// caminhos de arquivo e as linhas de comando antes de tocar na narrativa — o contrário da
/// prioridade certa.
/// </para>
/// </summary>
internal static class MemoryRender
{
    /// <summary>Marca de corte. Entra no bloco para o modelo saber que ali falta texto.</summary>
    public const string Marca = " …[truncado]";

    /// <summary>
    /// Devolve <paramref name="bloco"/> inteiro se ele couber, uma versão com o resumo aparado
    /// se não couber, ou string vazia quando nem o cabeçalho e os artefatos cabem sozinhos.
    /// </summary>
    public static string Fit(string bloco, string resumo, int maxTokens, TokenCounter counter)
    {
        if (maxTokens <= 0 || string.IsNullOrEmpty(bloco)) return "";
        if (counter.CountText(bloco) <= maxTokens) return bloco;

        string alvo = resumo?.Trim() ?? "";
        if (alvo.Length == 0) return "";

        int inicio = bloco.IndexOf(alvo, StringComparison.Ordinal);
        if (inicio < 0) return "";

        string antes = bloco[..inicio];
        string depois = bloco[(inicio + alvo.Length)..];

        // O que o resumo pode ocupar é o que sobra depois do que não se corta.
        int folga = maxTokens
                  - counter.CountText(antes)
                  - counter.CountText(depois)
                  - counter.CountText(Marca);

        if (folga <= 0) return "";

        string curto = PorTokens(alvo, folga, counter);
        if (curto.Length == 0) return "";

        return antes + curto + Marca + depois;
    }

    /// <summary>
    /// Maior prefixo de <paramref name="texto"/> que cabe em <paramref name="cota"/> tokens,
    /// cortado em fronteira de palavra.
    /// </summary>
    private static string PorTokens(string texto, int cota, TokenCounter counter)
    {
        if (counter.CountText(texto) <= cota) return texto;

        // Busca binária no comprimento em caracteres. A razão caractere/token varia com o
        // texto — acentos, pontuação, palavras longas —, então estimar uma vez erra para mais
        // ou para menos. São ~11 medições num resumo de mil caracteres, e só quando a cota já
        // estourou.
        int baixo = 0, alto = texto.Length;
        while (baixo < alto)
        {
            int meio = (baixo + alto + 1) / 2;
            if (counter.CountText(texto[..meio]) <= cota) baixo = meio;
            else alto = meio - 1;
        }

        if (baixo == 0) return "";

        // Recua até o último espaço para não entregar meia palavra. Só vale a pena se o recuo
        // não jogar fora mais da metade do que coube.
        int espaco = texto.LastIndexOf(' ', baixo - 1);
        if (espaco > baixo / 2) baixo = espaco;

        return texto[..baixo].TrimEnd();
    }
}
