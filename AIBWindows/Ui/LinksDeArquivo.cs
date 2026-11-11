using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using Markdig.Wpf;

namespace AIB.Ui;

/// <summary>
/// Caminho de arquivo citado na fala da IA vira link: o clique abre o Explorer com o arquivo
/// selecionado.
/// <para>
/// Pedido do usuário (29/10/2026): a Ellen cita o arquivo que acabou de editar ou mover, e para
/// chegar até ele era preciso copiar o caminho e colar no Explorer. Vale para a janela principal e
/// para o Shadow, que desenham a fala pelo mesmo <see cref="VisorDeMarkdown"/>.
/// </para>
/// <para>
/// SÓ O QUE EXISTE vira link: um caminho inventado, ou de um arquivo que já foi apagado, fica
/// texto. E o clique NUNCA abre o arquivo — só o mostra no Explorer. O texto é do modelo, e um
/// link que executasse um <c>.exe</c> ou <c>.ps1</c> citado na fala seria um atalho para rodar
/// algo sem cartão.
/// </para>
/// <para>
/// Duas formas de citar: o caminho absoluto (<c>C:\Users\...\relatorio.md</c>), e o nome solto
/// (<c>relatorio.md</c>) quando ele é o nome de UM arquivo recente da conversa — lido, gravado,
/// editado ou movido. Dois recentes com o mesmo nome: nenhum vira link, porque não dá para saber
/// qual é.
/// </para>
/// </summary>
public static class LinksDeArquivo
{
    /// <summary>Um trecho do texto que é caminho, e o caminho completo para onde ele leva.</summary>
    public readonly record struct Trecho(int Inicio, int Tamanho, string Caminho);

    // Caminho absoluto com letra de unidade. Espaço é permitido (nomes como "Proposta Comercial
    // - Vouler 1.pdf"), então o casamento vai até o fim da linha e é encurtado depois, palavra a
    // palavra, até sobrar um caminho que existe.
    private static readonly Regex Absoluto = new(@"[A-Za-z]:[\\/][^\r\n\t""<>|*?`]*", RegexOptions.Compiled);

    /// <summary>Teto de tentativas de encurtar um casamento: o disco é consultado em cada uma.</summary>
    private const int TetoDeCortes = 30;

    /// <summary>Teto do texto examinado. Uma fala gigante não pode travar a tela.</summary>
    private const int TetoDoTexto = 20_000;

    private static readonly char[] Sobras = { '.', ',', ';', ':', '!', '?', ')', ']', '}', '\'', '"', ' ', '»', '”' };

    /// <summary>
    /// Os trechos de <paramref name="texto"/> que são caminho existente. <paramref name="existe"/>
    /// diz se um caminho está no disco; <paramref name="recentes"/> são os arquivos que a conversa
    /// tocou, para o nome solto.
    /// </summary>
    public static IReadOnlyList<Trecho> Achar(string? texto, Func<string, bool> existe, IReadOnlyList<string> recentes)
    {
        var saida = new List<Trecho>();
        if (string.IsNullOrEmpty(texto) || texto.Length > TetoDoTexto) return saida;

        // Recomeça logo depois de cada achado: o casamento vai até o fim da linha, e um segundo
        // caminho na mesma linha ficaria dentro do primeiro.
        for (var m = Absoluto.Match(texto); m.Success; )
        {
            if (MaiorExistente(m.Value, existe) is { } achado)
            {
                saida.Add(new Trecho(m.Index, achado.Length, Completo(achado)));
                m = Absoluto.Match(texto, m.Index + achado.Length);
            }
            else
            {
                m = Absoluto.Match(texto, m.Index + 3);
            }
        }

        foreach (var (nome, caminho) in NomesUnicos(recentes))
        {
            int i = 0;
            while ((i = texto.IndexOf(nome, i, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int fim = i + nome.Length;
                bool borda = (i == 0 || !Parte(texto[i - 1])) && (fim == texto.Length || !Parte(texto[fim]));
                bool livre = !saida.Any(t => i < t.Inicio + t.Tamanho && fim > t.Inicio);

                if (borda && livre && existe(caminho)) saida.Add(new Trecho(i, nome.Length, caminho));
                i = fim;
            }
        }

        return saida.OrderBy(t => t.Inicio).ToList();
    }

    /// <summary>Letra, dígito, barra ou ponto colado no nome: ali o nome é parte de outra coisa.</summary>
    private static bool Parte(char c) => char.IsLetterOrDigit(c) || c is '\\' or '/' or '_' or '-' or '.';

    /// <summary>
    /// O maior começo do casamento que existe no disco, cortando nas fronteiras de palavra e
    /// tirando pontuação do fim ("C:\x\a.md, e depois" → "C:\x\a.md").
    /// </summary>
    private static string? MaiorExistente(string bruto, Func<string, bool> existe)
    {
        var cortes = new List<int> { bruto.Length };
        for (int i = bruto.Length - 1; i > 3 && cortes.Count < TetoDeCortes; i--)
            if (bruto[i] == ' ') cortes.Add(i);

        foreach (int corte in cortes)
        {
            string candidato = bruto[..corte].TrimEnd(Sobras);
            if (candidato.Length <= 3) continue;
            if (existe(candidato)) return candidato;
        }

        return null;
    }

    private static string Completo(string caminho)
    {
        try { return Path.GetFullPath(caminho).TrimEnd('\\', '/'); }
        catch { return caminho; }
    }

    /// <summary>Nome → caminho, só para os nomes que aparecem UMA vez entre os recentes.</summary>
    private static IEnumerable<(string Nome, string Caminho)> NomesUnicos(IReadOnlyList<string> recentes) =>
        recentes
            .Select(c => (Nome: Path.GetFileName(c.TrimEnd('\\', '/')), Caminho: c))
            .Where(x => x.Nome.Length >= 3 && x.Nome.Contains('.'))
            .GroupBy(x => x.Nome, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Caminho).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            .Select(g => g.First());

    // ─────────────────────────────────────────────────────────────────────────
    // O documento
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Liga os links ao visualizador: agora e a cada documento novo (o streaming troca o
    /// documento a cada pedaço da resposta).
    /// </summary>
    public static void Ligar(MarkdownViewer viewer)
    {
        void Aplicar()
        {
            if (viewer.Document is not { } doc) return;
            try { LinksDeArquivo.Aplicar(doc, Existe, Services.ContextService.CaminhosRecentes()); }
            catch (Exception ex) { Console.WriteLine($"[LINKS] {ex.Message}"); } // sem link, o texto fica
        }

        Aplicar();
        DependencyPropertyDescriptor
            .FromProperty(MarkdownViewer.DocumentProperty, typeof(MarkdownViewer))
            ?.AddValueChanged(viewer, (_, _) => Aplicar());
    }

    /// <summary>Troca, em cada trecho de texto do documento, os caminhos por links.</summary>
    public static void Aplicar(FlowDocument doc, Func<string, bool> existe, IReadOnlyList<string> recentes)
    {
        var runs = new List<Run>();
        foreach (var bloco in doc.Blocks) Coletar(bloco, runs);

        foreach (var run in runs)
        {
            var trechos = Achar(run.Text, existe, recentes);
            if (trechos.Count > 0) Trocar(run, trechos);
        }
    }

    /// <summary>Os <see cref="Run"/> do documento, fora dos links que já existem.</summary>
    private static void Coletar(object no, List<Run> runs)
    {
        switch (no)
        {
            case Paragraph p: foreach (var i in p.Inlines) Coletar(i, runs); break;
            case Section s: foreach (var b in s.Blocks) Coletar(b, runs); break;
            case List l: foreach (var item in l.ListItems) foreach (var b in item.Blocks) Coletar(b, runs); break;
            case Table t:
                foreach (var g in t.RowGroups) foreach (var r in g.Rows) foreach (var c in r.Cells)
                    foreach (var b in c.Blocks) Coletar(b, runs);
                break;
            case Hyperlink: break;
            case Span sp: foreach (var i in sp.Inlines) Coletar(i, runs); break;
            case Run r: runs.Add(r); break;
        }
    }

    private static readonly DependencyProperty[] Aparencia =
    {
        TextElement.FontFamilyProperty, TextElement.FontSizeProperty, TextElement.FontWeightProperty,
        TextElement.FontStyleProperty, TextElement.ForegroundProperty, TextElement.BackgroundProperty,
        Inline.TextDecorationsProperty, Inline.BaselineAlignmentProperty
    };

    /// <summary>Um Run com o texto dado e a mesma aparência do original (estilo e valores locais).</summary>
    private static Run Copia(Run origem, string texto)
    {
        var r = new Run(texto) { Style = origem.Style };
        foreach (var p in Aparencia)
        {
            object v = origem.ReadLocalValue(p);
            if (v != DependencyProperty.UnsetValue) r.SetValue(p, v);
        }
        return r;
    }

    /// <summary>O Run vira: texto antes, link, texto entre, link... texto depois.</summary>
    private static void Trocar(Run run, IReadOnlyList<Trecho> trechos)
    {
        InlineCollection? irmaos = run.Parent switch
        {
            Paragraph p => p.Inlines,
            Span s => s.Inlines,
            _ => null
        };
        if (irmaos == null) return;

        string texto = run.Text;
        int pos = 0;

        foreach (var t in trechos)
        {
            if (t.Inicio < pos) continue;
            if (t.Inicio > pos) irmaos.InsertBefore(run, Copia(run, texto[pos..t.Inicio]));
            irmaos.InsertBefore(run, Link(Copia(run, texto.Substring(t.Inicio, t.Tamanho)), t.Caminho));
            pos = t.Inicio + t.Tamanho;
        }

        if (pos < texto.Length) irmaos.InsertBefore(run, Copia(run, texto[pos..]));
        irmaos.Remove(run);
    }

    private static Hyperlink Link(Run texto, string caminho)
    {
        // A cor do link vence a do código inline: o caminho entre crases também tem de parecer clicável.
        texto.SetResourceReference(TextElement.ForegroundProperty, "AccentBlueBrush");

        // Sem o fundo do código inline. Visto no uso (30/10/2026): o caminho entre crases virava
        // link azul sobre um bloco branco, destoando do vidro escuro.
        texto.Background = System.Windows.Media.Brushes.Transparent;

        var link = new Hyperlink(texto)
        {
            ToolTip = caminho + "\nClique para mostrar no Explorer",
            TextDecorations = TextDecorations.Underline,
            Tag = caminho
        };
        link.Click += (_, _) => Mostrar(caminho);
        return link;
    }

    /// <summary>
    /// Mostra no Explorer: arquivo selecionado na pasta dele; pasta, aberta. Nunca abre nem
    /// executa o arquivo. Sumiu desde a fala: abre a pasta que sobrou, se sobrou.
    /// </summary>
    public static void Mostrar(string caminho)
    {
        try
        {
            if (File.Exists(caminho))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{caminho}\"");
            else if (Directory.Exists(caminho))
                System.Diagnostics.Process.Start("explorer.exe", $"\"{caminho}\"");
            else if (Path.GetDirectoryName(caminho) is { } pai && Directory.Exists(pai))
                System.Diagnostics.Process.Start("explorer.exe", $"\"{pai}\"");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LINKS] Não abriu o Explorer em '{caminho}': {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // O disco, com memória curta
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly ConcurrentDictionary<string, (bool Existe, DateTime Quando)> Visto = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Se o caminho existe, lembrado por 10 s. O streaming remonta o documento a cada pedaço da
    /// resposta, e sem isto cada pedaço consultaria o disco para os mesmos caminhos.
    /// </summary>
    public static bool Existe(string caminho)
    {
        var agora = DateTime.UtcNow;
        if (Visto.TryGetValue(caminho, out var v) && agora - v.Quando < TimeSpan.FromSeconds(10)) return v.Existe;

        bool existe;
        try { existe = File.Exists(caminho) || Directory.Exists(caminho); }
        catch { existe = false; }

        if (Visto.Count > 2000) Visto.Clear();
        Visto[caminho] = (existe, agora);
        return existe;
    }
}
