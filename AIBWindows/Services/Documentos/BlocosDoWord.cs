using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AIB.Services.Documentos;

/// <summary>
/// Os blocos de um .docx, numerados — o MESMO percurso para quem lê e para quem edita.
/// <para>
/// O "¶12" que o modelo vê no 'read' é o que ele manda de volta no 'edit'. Se o leitor e o
/// editor contassem cada um do seu jeito (um pulando parágrafo vazio, o outro não), o modelo
/// apagaria o parágrafo vizinho do que pediu. Por isso a contagem mora só aqui.
/// </para>
/// <para>
/// Cabeçalho, rodapé, notas e caixas de texto ficam de fora: são partes à parte do corpo, e a
/// edição só sabe apontar para o corpo.
/// </para>
/// </summary>
internal static class BlocosDoWord
{
    /// <summary>Um bloco lido e o elemento do XML de onde veio (Paragraph ou Table).</summary>
    internal sealed record Item(Bloco Bloco, OpenXmlElement Elemento);

    public static List<Item> Ler(WordprocessingDocument doc)
    {
        var body = doc.MainDocumentPart?.Document?.Body;
        var lista = new List<Item>();
        if (body == null) return lista;

        var estilos = new Estilos(doc.MainDocumentPart!);
        string? secao = null;
        int n = 0;

        foreach (var el in Elementos(body))
        {
            if (el is Table t)
            {
                // Tabela com todas as linhas apagadas em revisão: na versão aceita, ela não existe.
                var linhas = Linhas(t);
                if (linhas.Count > 0) lista.Add(new Item(new Bloco(++n, TipoDeBloco.Tabela, 0, "", linhas, secao), t));
                continue;
            }

            var p = (Paragraph)el;
            string texto = Texto(p);
            if (string.IsNullOrWhiteSpace(texto)) continue;

            var (tipo, nivel) = estilos.Classificar(p);
            lista.Add(new Item(new Bloco(++n, tipo, nivel, texto, null, secao), p));
            if (tipo == TipoDeBloco.Titulo) secao = texto.Trim();
        }

        return lista;
    }

    /// <summary>Parágrafos e tabelas do corpo, na ordem, entrando nos controles de conteúdo.</summary>
    private static IEnumerable<OpenXmlElement> Elementos(OpenXmlElement pai)
    {
        foreach (var el in pai.ChildElements)
        {
            switch (el)
            {
                case Paragraph or Table:
                    yield return el;
                    break;
                case SdtBlock sdt when sdt.SdtContentBlock != null:
                    foreach (var dentro in Elementos(sdt.SdtContentBlock)) yield return dentro;
                    break;
            }
        }
    }

    /// <summary>As linhas como ficam com as revisões aceitas: linha apagada (w:trPr/w:del) não entra.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Linhas(Table t) =>
        t.Elements<TableRow>()
         .Where(r => r.GetFirstChild<TableRowProperties>()?.GetFirstChild<Deleted>() == null)
         .Select(r => (IReadOnlyList<string>)r.Elements<TableCell>().Select(TextoDaCelula).ToList())
         .ToList();

    public static string TextoDaCelula(TableCell c) =>
        string.Join(" / ", c.Descendants<Paragraph>().Select(Texto).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));

    // ─────────────────────────────────────────────────────────────────────────
    // Texto de um parágrafo
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Um pedaço do texto: um <see cref="Text"/> (editável) ou um tab/quebra, que vale um espaço.
    /// </summary>
    internal sealed record Pedaco(OpenXmlElement Elemento, string Texto, int Inicio)
    {
        public int Fim => Inicio + Texto.Length;
        public bool Editavel => Elemento is Text;
    }

    /// <summary>
    /// Os pedaços do parágrafo, na ordem. Texto de revisão apagada (w:del) não entra: o usuário
    /// não o vê no documento, e um 'replace' que o achasse trocaria um trecho invisível.
    /// Texto inserido (w:ins) entra, porque está na página.
    /// </summary>
    public static List<Pedaco> Pedacos(Paragraph p)
    {
        var lista = new List<Pedaco>();
        int pos = 0;
        foreach (var el in p.Descendants())
        {
            string? s = el switch
            {
                Text t => t.Text,
                TabChar or Break or CarriageReturn => " ",
                _ => null
            };
            if (s == null || !DoParagrafo(el, p)) continue;
            lista.Add(new Pedaco(el, s, pos));
            pos += s.Length;
        }
        return lista;
    }

    public static string Texto(Paragraph p)
    {
        var sb = new StringBuilder();
        foreach (var x in Pedacos(p)) sb.Append(x.Texto);
        return sb.ToString();
    }

    /// <summary>Fora de revisão apagada e de parágrafo aninhado (caixa de texto dentro de um desenho).</summary>
    private static bool DoParagrafo(OpenXmlElement el, Paragraph p)
    {
        for (var a = el.Parent; a != null && a != p; a = a.Parent)
            if (a is DeletedRun or MoveFromRun or Paragraph) return false;
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Estilos
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Título e lista pelo NOME do estilo. No Word em português o id do "Título 1" é "Ttulo1", não
    /// "Heading1" (visto no spike com um arquivo real); o nome interno continua "heading 1".
    /// </summary>
    internal sealed class Estilos
    {
        private static readonly Regex NomeDeTitulo = new(@"^heading\s*(\d)$", RegexOptions.IgnoreCase);
        private static readonly Regex IdDeTitulo = new(@"^(Heading|Ttulo|Titulo|Título)(\d)$", RegexOptions.IgnoreCase);

        private readonly Dictionary<string, Style> _porId = new();

        public Estilos(MainDocumentPart main)
        {
            foreach (var s in main.StyleDefinitionsPart?.Styles?.Elements<Style>() ?? Enumerable.Empty<Style>())
                if (s.StyleId?.Value is { } id) _porId.TryAdd(id, s);
        }

        public (TipoDeBloco, int) Classificar(Paragraph p)
        {
            string? id = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
            _porId.TryGetValue(id ?? "", out var estilo);
            string nome = estilo?.StyleName?.Val?.Value ?? "";

            if (Titulo(id, nome) is int nivel) return (TipoDeBloco.Titulo, nivel);

            var num = p.ParagraphProperties?.NumberingProperties ?? estilo?.StyleParagraphProperties?.NumberingProperties;
            bool lista = num != null && num.NumberingId?.Val?.Value != 0;
            if (lista || nome.StartsWith("List", StringComparison.OrdinalIgnoreCase))
                return (TipoDeBloco.Item, (num?.NumberingLevelReference?.Val?.Value ?? 0) + 1);

            return (TipoDeBloco.Paragrafo, 0);
        }

        private static int? Titulo(string? id, string nome)
        {
            if (nome.Equals("Title", StringComparison.OrdinalIgnoreCase)) return 1;
            if (NomeDeTitulo.Match(nome) is { Success: true } m) return int.Parse(m.Groups[1].Value);
            if (nome.Length > 0) return null;

            // Sem definição de estilo (documento gerado por outro programa): sobra o id.
            if (id == null) return null;
            if (id.Equals("Title", StringComparison.OrdinalIgnoreCase)) return 1;
            return IdDeTitulo.Match(id) is { Success: true } i ? int.Parse(i.Groups[2].Value) : null;
        }
    }
}
