using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AIB.Services.Documentos;

/// <summary>O que uma linha do plano mostra no cartão.</summary>
public enum TipoDeMudanca
{
    /// <summary>Uma célula (ou um trecho de documento): antes e depois.</summary>
    Valor,

    /// <summary>Uma linha inteira que sai.</summary>
    Sai,

    /// <summary>Uma linha inteira que entra.</summary>
    Entra,

    /// <summary>Cabeçalho de um grupo ("Coluna D · a mesma fórmula nas 200 linhas").</summary>
    Grupo,

    /// <summary>O resumo do que não coube ("+ 197 células na coluna D").</summary>
    Mais
}

/// <summary>
/// Uma linha do plano.
/// </summary>
/// <param name="Onde">Célula ("C14"), linha ("18") ou lugar no documento ("Seção 2 · ¶3").</param>
/// <param name="Antes">Null quando não havia nada (célula vazia, linha nova).</param>
/// <param name="Depois">Null quando sai. Fórmula vem com o resultado: "=C15*12 → 7.680,00".</param>
/// <param name="Erro">O depois é um erro de fórmula (#N/D, #REF!): o cartão pinta de vermelho.</param>
/// <param name="Grupo">Grupo de expansão no cartão (1, 2, 3...). 0 quando não agrupado.</param>
/// <param name="Extra">Linha escondida inicialmente no cartão, revelada ao expandir o Grupo.</param>
/// <param name="AmostraAntes">Formatação: a célula como o Excel a mostra hoje. O texto de Antes descreve o mesmo em palavras.</param>
/// <param name="AmostraDepois">Formatação: a célula como vai ficar.</param>
public sealed record Mudanca(TipoDeMudanca Tipo, string Onde, string? Antes, string? Depois, bool Erro = false, int Grupo = 0, bool Extra = false,
                             Aparencia? AmostraAntes = null, Aparencia? AmostraDepois = null);

/// <summary>Bordas de uma célula, na amostra do cartão.</summary>
[System.Flags]
public enum Bordas
{
    Nenhuma = 0,
    Cima = 1,
    Baixo = 2,
    Esquerda = 4,
    Direita = 8,

    /// <summary>A borda de baixo é dupla (a de total, no Excel).</summary>
    BaixoDupla = 16
}

/// <summary>
/// Como uma célula aparece no Excel: a amostra que o cartão desenha ao lado da descrição.
/// <para>
/// Mockup "Formatação de planilhas do AIB" (19/10/2026): "fundo amarelo claro" não diz se o
/// amarelo é o que a pessoa quer; a célula desenhada diz. A descrição em palavras fica embaixo,
/// para quem não distingue as cores.
/// </para>
/// </summary>
/// <param name="Texto">O valor da célula já com o formato de número aplicado ("R$ 1.180,00").</param>
/// <param name="Cor">Cor do texto, "#RRGGBB". Null: a do Excel (preto).</param>
/// <param name="Fundo">Cor do fundo, "#RRGGBB". Null: sem preenchimento.</param>
/// <param name="Alinhamento">"left", "center" ou "right".</param>
/// <param name="Escala">Escala de cores ou barra de dados: as cores, da menor para a maior.</param>
public sealed record Aparencia(
    string Texto,
    bool Negrito = false,
    bool Italico = false,
    bool Sublinhado = false,
    string? Cor = null,
    string? Fundo = null,
    string Alinhamento = "left",
    Bordas Bordas = Bordas.Nenhuma,
    IReadOnlyList<string>? Escala = null);

/// <summary>
/// O que uma gravação de documento VAI fazer, calculado antes de o cartão aparecer.
/// <para>
/// Pedido do usuário (19/10/2026, mockup "Edição de documentos do AIB"): ver exatamente o que
/// muda, célula por célula ou trecho por trecho, antes de permitir. O mesmo plano que o cartão
/// mostra é o que a execução confere: se o arquivo mudou entre a pergunta e o "Permitir"
/// (<see cref="Impressao"/>), nada é gravado.
/// </para>
/// </summary>
public sealed class PlanoDeGravacao
{
    /// <summary>Quantas mudanças o cartão mostra antes de resumir.</summary>
    public const int Visiveis = 12;

    /// <summary>Limite de mudanças mostradas no cartão por bloco, contando extras.</summary>
    public const int NoCartao = 100;

    /// <summary>Caminho absoluto do arquivo.</summary>
    public string Arquivo { get; init; } = "";

    /// <summary>Arquivo novo (cartão laranja). Falso: mexe num que existe (vermelho).</summary>
    public bool Criar { get; init; }

    /// <summary>
    /// A edição só muda a aparência (estilo, largura, painel, formatação condicional): nenhum
    /// valor, linha ou aba. O cartão fica laranja (decisão do usuário, 19/10/2026).
    /// </summary>
    public bool SoAparencia { get; init; }

    /// <summary>A pergunta do cartão: "Gravar 4 células nesta planilha?".</summary>
    public string Titulo { get; init; } = "";

    /// <summary>O subtítulo do cartão: o que acontece com o que já existe.</summary>
    public string Consequencia { get; init; } = "";

    /// <summary>A linha cinza abaixo do caminho: "aba "Outubro" · 4 células".</summary>
    public string Resumo { get; init; } = "";

    /// <summary>Pílulas de contagem, na edição em massa: "D2:D201 · 200 células · fórmula".</summary>
    public List<string> Contagens { get; init; } = new();

    /// <summary>Até <see cref="Visiveis"/> mudanças, já agrupadas e resumidas.</summary>
    public List<Mudanca> Mudancas { get; init; } = new();

    /// <summary>Fórmulas que JÁ existiam e mudam de valor: "C42 total: 12.400,00 → 13.150,00".</summary>
    public List<string> Efeitos { get; init; } = new();

    /// <summary>Avisos laranja: fórmula que vai dar #REF!, linhas que sobem.</summary>
    public List<string> Avisos { get; init; } = new();

    /// <summary>Nome da cópia guardada antes de gravar. Null em arquivo novo.</summary>
    public string? Copia { get; init; }

    /// <summary>Prévia de arquivo novo: abas e primeiras linhas, já em texto de grade.</summary>
    public PreviaDeArquivo? Previa { get; init; }

    /// <summary>
    /// SHA-256 do arquivo quando o plano foi feito. Null em arquivo novo. A execução recusa se o
    /// arquivo mudou desde então: o usuário autorizou ESTE antes e depois, não outro.
    /// </summary>
    public string? Impressao { get; init; }

    /// <summary>
    /// O plano em texto — vai para o <c>ScriptBody</c> do cartão, que o histórico de ações
    /// guarda, e serve de cartão para quem não sabe desenhar a tabela.
    /// </summary>
    public string EmTexto()
    {
        var sb = new StringBuilder();
        if (Resumo.Length > 0) sb.AppendLine(Resumo);
        foreach (var c in Contagens) sb.AppendLine("· " + c);

        foreach (var m in Mudancas)
        {
            sb.AppendLine(m.Tipo switch
            {
                TipoDeMudanca.Grupo => $"[{m.Onde}]",
                TipoDeMudanca.Mais => m.Onde,
                TipoDeMudanca.Sai => $"- {m.Onde}: {m.Antes}",
                TipoDeMudanca.Entra => $"+ {m.Onde}: {m.Depois}",
                _ => $"{m.Onde}: {m.Antes ?? "(vazia)"} → {m.Depois ?? "(vazia)"}"
            });
        }

        if (Efeitos.Count > 0)
        {
            if (!SoAparencia) sb.AppendLine("Efeito nas fórmulas que já existem:");
            foreach (var e in Efeitos) sb.AppendLine("  " + e);
        }

        foreach (var a in Avisos) sb.AppendLine("! " + a);
        if (Previa != null) sb.Append(Previa.EmTexto());
        if (Copia != null) sb.AppendLine($"Cópia antes de gravar: {Copia}");

        return sb.ToString().TrimEnd();
    }
}

/// <summary>A prévia de um arquivo novo: as abas (ou seções) e as primeiras linhas.</summary>
/// <param name="Partes">Nomes das abas; vazio em documento.</param>
/// <param name="Linhas">As primeiras linhas da primeira parte, célula por célula.</param>
/// <param name="Omitidas">Quantas linhas não couberam.</param>
public sealed record PreviaDeArquivo(IReadOnlyList<string> Partes, IReadOnlyList<IReadOnlyList<string>> Linhas, int Omitidas)
{
    public string EmTexto()
    {
        var sb = new StringBuilder();
        if (Partes.Count > 0) sb.AppendLine("Abas: " + string.Join(", ", Partes));
        foreach (var l in Linhas) sb.AppendLine(string.Join(" | ", l));
        if (Omitidas > 0) sb.AppendLine($"+ {Omitidas} linhas");
        return sb.ToString();
    }

    public static PreviaDeArquivo De(IEnumerable<string> partes, IEnumerable<IEnumerable<string>> linhas, int mostrar = 6)
    {
        var todas = linhas.Select(l => (IReadOnlyList<string>)l.ToList()).ToList();
        return new PreviaDeArquivo(partes.ToList(), todas.Take(mostrar).ToList(), System.Math.Max(0, todas.Count - mostrar));
    }
}
