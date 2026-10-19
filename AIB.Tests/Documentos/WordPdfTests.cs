using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AIB.Services.Documentos;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace AIB.Tests;

/// <summary>Leitura de .docx/.pdf e edição de .docx pela ferramenta <c>doc</c>. Arquivos gerados em pasta temporária.</summary>
public sealed class WordPdfTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "aib-doc-" + Guid.NewGuid());

    public WordPdfTests() => Directory.CreateDirectory(_pasta);

    public void Dispose()
    {
        try { Directory.Delete(_pasta, true); } catch { }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Montagem de arquivos
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Um .docx como o Word em português grava: o "Título 1" tem id "Ttulo1" e nome "heading 1".
    /// </summary>
    private string Docx(string nome, params OpenXmlElement[] corpo)
    {
        string caminho = Path.Combine(_pasta, nome);
        using var doc = WordprocessingDocument.Create(caminho, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.AddNewPart<StyleDefinitionsPart>().Styles = new Styles(
            new Style(new StyleName { Val = "Normal" }) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
            new Style(new StyleName { Val = "heading 1" }, new BasedOn { Val = "Normal" }) { Type = StyleValues.Paragraph, StyleId = "Ttulo1" },
            new Style(new StyleName { Val = "heading 2" }, new BasedOn { Val = "Normal" }) { Type = StyleValues.Paragraph, StyleId = "Ttulo2" },
            new Style(new StyleName { Val = "List Paragraph" }, new BasedOn { Val = "Normal" }) { Type = StyleValues.Paragraph, StyleId = "PargrafodaLista" });
        main.Document = new Document(new Body(corpo));
        return caminho;
    }

    private static Paragraph P(string texto, string? estilo = null)
    {
        var p = new Paragraph();
        if (estilo != null) p.Append(new ParagraphProperties(new ParagraphStyleId { Val = estilo }));
        if (texto.Length > 0) p.Append(R(texto));
        return p;
    }

    private static Run R(string texto, bool negrito = false)
    {
        var r = new Run();
        if (negrito) r.Append(new RunProperties(new Bold()));
        r.Append(new Text(texto) { Space = SpaceProcessingModeValues.Preserve });
        return r;
    }

    private static Paragraph Item(string texto) =>
        new(new ParagraphProperties(new ParagraphStyleId { Val = "PargrafodaLista" },
                                    new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 1 })),
            R(texto));

    private static Table Tabela(params string[][] linhas)
    {
        var t = new Table(new TableProperties(), new TableGrid(linhas[0].Select(_ => new GridColumn { Width = "3000" })));
        t.Append(linhas.Select(l => new TableRow(l.Select(c => new TableCell(P(c))))));
        return t;
    }

    /// <summary>Documento-padrão dos testes: título, parágrafo, item, tabela, subtítulo, parágrafo.</summary>
    private string Relatorio() => Docx("relatorio.docx",
        P("Escopo", "Ttulo1"),                               // ¶1
        P("O projeto atende três setores."),                 // ¶2
        P(""),                                               // vazio: não conta
        Item("Financeiro"),                                  // ¶3
        Tabela(new[] { "Setor", "Ramal" }, new[] { "TI", "2101" }), // ¶4
        P("Prazos", "Ttulo2"),                               // ¶5
        P("A entrega é em março."));                         // ¶6

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ─────────────────────────────────────────────────────────────────────────
    // Leitor
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OsBlocos_SaemNaOrdem_ComNumeracaoSequencial_SemOParagrafoVazio()
    {
        var doc = LeitorDeDocumento.Abrir(Relatorio());

        doc.Formato.Should().Be(Formato.Docx);
        doc.Blocos.Select(b => b.Numero).Should().Equal(1, 2, 3, 4, 5, 6);
        doc.Blocos.Select(b => b.Tipo).Should().Equal(
            TipoDeBloco.Titulo, TipoDeBloco.Paragrafo, TipoDeBloco.Item, TipoDeBloco.Tabela, TipoDeBloco.Titulo, TipoDeBloco.Paragrafo);
    }

    [Fact]
    public void OTitulo_SeReconhecePeloNomeDoEstilo_MesmoComIdTtulo1DoWordEmPortugues()
    {
        // Spike de 19/10/2026: no Word pt-BR o id é "Ttulo1"; quem procurasse "Heading1" não acharia título nenhum.
        var doc = LeitorDeDocumento.Abrir(Relatorio());

        doc.Blocos[0].Should().Match<Bloco>(b => b.Tipo == TipoDeBloco.Titulo && b.Nivel == 1 && b.Texto == "Escopo");
        doc.Blocos[4].Should().Match<Bloco>(b => b.Tipo == TipoDeBloco.Titulo && b.Nivel == 2 && b.Texto == "Prazos");
    }

    [Fact]
    public void OItemDeLista_ViraItemDeNivel1()
    {
        var b = LeitorDeDocumento.Abrir(Relatorio()).Blocos[2];

        b.Tipo.Should().Be(TipoDeBloco.Item);
        b.Nivel.Should().Be(1);
        b.Texto.Should().Be("Financeiro");
    }

    [Fact]
    public void ATabela_VemCelulaPorCelula()
    {
        var b = LeitorDeDocumento.Abrir(Relatorio()).Blocos[3];

        b.Tipo.Should().Be(TipoDeBloco.Tabela);
        b.Texto.Should().BeEmpty();
        b.Tabela!.Select(l => string.Join("|", l)).Should().Equal("Setor|Ramal", "TI|2101");
    }

    [Fact]
    public void ASecao_EOTituloMaisRecenteAcima()
    {
        var doc = LeitorDeDocumento.Abrir(Relatorio());

        doc.Blocos[0].Secao.Should().BeNull();
        doc.Blocos[1].Secao.Should().Be("Escopo");
        doc.Blocos[3].Secao.Should().Be("Escopo");
        doc.Blocos[5].Secao.Should().Be("Prazos");
    }

    [Fact]
    public void ARevisaoApagada_NaoEntraNoTexto_EAInseridaEntra()
    {
        // Com "controlar alterações" ligado, o texto apagado continua no XML (w:delText).
        string caminho = Docx("revisao.docx", new Paragraph(
            R("Valor: "),
            new DeletedRun(new Run(new DeletedText("100"))) { Id = "1", Author = "A" },
            new InsertedRun(R("200")) { Id = "2", Author = "A" },
            R(" reais")));

        LeitorDeDocumento.Abrir(caminho).Blocos.Single().Texto.Should().Be("Valor: 200 reais");
    }

    [Fact]
    public void OPdf_SeLePorPagina_ComAcento()
    {
        string caminho = Path.Combine(_pasta, "duas.pdf");
        var builder = new PdfDocumentBuilder();
        var arial = builder.AddTrueTypeFont(File.ReadAllBytes(@"C:\Windows\Fonts\arial.ttf"));
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("Primeira página.", 12, new PdfPoint(50, 750), arial);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("Segunda: ação e reunião.", 12, new PdfPoint(50, 750), arial);
        File.WriteAllBytes(caminho, builder.Build());

        var doc = LeitorDeDocumento.Abrir(caminho);

        doc.Formato.Should().Be(Formato.Pdf);
        doc.Paginas.Should().Be(2);
        doc.Blocos.Select(b => (b.Numero, b.Tipo)).Should().Equal((1, TipoDeBloco.Pagina), (2, TipoDeBloco.Pagina));
        doc.Blocos[0].Texto.Should().Contain("Primeira página.");
        doc.Blocos[1].Texto.Should().Contain("ação e reunião");
    }

    [Fact]
    public void ODocx_AbertoNoWord_AindaSeLe()
    {
        // O Word segura o arquivo aberto para escrita; abrir sem compartilhar daria "arquivo em uso".
        string caminho = Relatorio();
        using var word = new FileStream(caminho, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        LeitorDeDocumento.Abrir(caminho).Blocos.Should().HaveCount(6);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Editor
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OReplace_AtravessandoTresRuns_ViraRevisaoComONegritoDoPrimeiro_EReleCerto()
    {
        string caminho = Docx("runs.docx", new Paragraph(R("Prazo: "), R("30", negrito: true), R(" di"), R("as úteis.")));

        var g = EditorDeDocx.Planejar(caminho, Json("""[{"replace":"30 dias","with":"45 dias"}]"""), "Ana");
        g.Gravar();

        LeitorDeDocumento.Abrir(caminho).Blocos.Single().Texto.Should().Be("Prazo: 45 dias úteis.");
        using (var doc = WordprocessingDocument.Open(caminho, false))
        {
            var body = doc.MainDocumentPart!.Document!.Body!;
            var del = body.Descendants<DeletedRun>().ToList();
            string.Concat(del.SelectMany(d => d.Descendants<DeletedText>()).Select(t => t.Text)).Should().Be("30 dias");
            del.Should().OnlyContain(d => d.Author!.Value == "Ana");
            var ins = body.Descendants<InsertedRun>().Single();
            ins.Author!.Value.Should().Be("Ana");
            ins.InnerText.Should().Be("45 dias");
            ins.GetFirstChild<Run>()!.RunProperties?.Bold.Should().NotBeNull();
        }
        g.Resultado.Should().Be("1 alteração sugerida (controle de alterações)");
        g.Plano.Mudancas.Single().Should().Match<Mudanca>(m =>
            m.Tipo == TipoDeMudanca.Valor && m.Onde == "¶1" && m.Antes == "Prazo: 30 dias úteis." && m.Depois == "Prazo: 45 dias úteis.");
    }

    [Fact]
    public void OReplaceAmbiguo_DaErroComOsParagrafos()
    {
        string caminho = Docx("amb.docx", P("O prazo é curto."), P("Outro texto."), P("Este prazo também."));

        var acao = () => EditorDeDocx.Planejar(caminho, Json("""[{"replace":"prazo","with":"limite"}]"""));

        acao.Should().Throw<ErroDeDocumento>().WithMessage("*2 vezes*¶1, ¶3*");
    }

    [Fact]
    public void OReplaceComAll_TrocaTodas()
    {
        string caminho = Docx("todas.docx", P("O prazo é curto."), P("Este prazo também."));

        var g = EditorDeDocx.Planejar(caminho, Json("""[{"replace":"prazo","with":"limite","all":true}]"""));
        g.Gravar();

        LeitorDeDocumento.Abrir(caminho).Blocos.Select(b => b.Texto).Should().Equal("O limite é curto.", "Este limite também.");
    }

    [Fact]
    public void OReplaceInexistente_DaErroSugerindoFind()
    {
        var acao = () => EditorDeDocx.Planejar(Relatorio(), Json("""[{"replace":"abril","with":"maio"}]"""));

        acao.Should().Throw<ErroDeDocumento>().WithMessage("operação 1: não achei*find*");
    }

    [Fact]
    public void InsertEDelete_UsamANumeracaoOriginal_NaMesmaChamada()
    {
        // ¶2 sai e o texto entra depois do ¶5 ORIGINAL (o título "Prazos"), não do que viraria ¶5.
        string caminho = Relatorio();

        var g = EditorDeDocx.Planejar(caminho, Json("""
            [{"delete":2},{"insert_after":5,"text":"Primeira linha\nSegunda linha"},{"delete":6}]
            """));
        g.Gravar();

        LeitorDeDocumento.Abrir(caminho).Blocos.Select(b => b.Texto).Should().Equal(
            "Escopo", "Financeiro", "", "Prazos", "Primeira linha", "Segunda linha");
        g.Plano.Mudancas.Select(m => (m.Tipo, m.Onde)).Should().Equal(
            (TipoDeMudanca.Sai, "¶2"), (TipoDeMudanca.Entra, "depois do ¶5"), (TipoDeMudanca.Sai, "¶6"));
    }

    [Fact]
    public void OInsertComEstiloDeTitulo_ViraTitulo_EOReplaceNaSecaoDizOnde()
    {
        string caminho = Relatorio();

        var g = EditorDeDocx.Planejar(caminho, Json("""
            [{"insert_after":6,"text":"Riscos","style":"Heading1"},{"replace":"março","with":"abril"}]
            """));
        g.Gravar();

        var novo = LeitorDeDocumento.Abrir(caminho).Blocos[^1];
        novo.Should().Match<Bloco>(b => b.Tipo == TipoDeBloco.Titulo && b.Nivel == 1 && b.Texto == "Riscos");
        g.Plano.Mudancas[1].Onde.Should().Be("Seção Prazos · ¶6");
    }

    [Fact]
    public void ACelulaDaTabela_TrocaOTexto()
    {
        string caminho = Relatorio();

        var g = EditorDeDocx.Planejar(caminho, Json("""[{"table":1,"row":2,"col":2,"value":"2109"}]"""));
        g.Gravar();

        LeitorDeDocumento.Abrir(caminho).Blocos[3].Tabela![1].Should().Equal("TI", "2109");
        g.Plano.Mudancas.Single().Should().Be(new Mudanca(TipoDeMudanca.Valor, "Tabela 1, linha 2, coluna 2", "2101", "2109"));
    }

    [Fact]
    public void OReplaceDentroDeTabela_DizLinhaEColuna()
    {
        var g = EditorDeDocx.Planejar(Relatorio(), Json("""[{"replace":"2101","with":"2109"}]"""));

        g.Plano.Mudancas.Single().Onde.Should().Be("Tabela ¶4, linha 2, coluna 2");
    }

    [Fact]
    public void ANumeroForaDoDocumento_DaErroComAOperacao()
    {
        var acao = () => EditorDeDocx.Planejar(Relatorio(), Json("""[{"delete":1},{"delete":40}]"""));

        acao.Should().Throw<ErroDeDocumento>().WithMessage("operação 2: não há ¶40*");
    }

    [Fact]
    public void PlanejarDuasVezes_DaOMesmoPlano()
    {
        // A execução refaz o plano e compara com o que o cartão mostrou.
        string caminho = Relatorio();
        var ops = Json("""[{"replace":"três","with":"quatro"},{"insert_after":3,"text":"RH","style":"List"},{"delete":6}]""");

        string a = EditorDeDocx.Planejar(caminho, ops).Plano.EmTexto();
        string b = EditorDeDocx.Planejar(caminho, ops).Plano.EmTexto();

        a.Should().Be(b);
    }

    [Fact]
    public void OPlano_TemCopiaImpressaoETitulo()
    {
        string caminho = Relatorio();

        var plano = EditorDeDocx.Planejar(caminho, Json("""[{"replace":"três","with":"quatro"},{"delete":6}]"""), "Ana").Plano;

        plano.Criar.Should().BeFalse();
        plano.Copia.Should().Be(CopiasDeDocumento.Descricao(caminho));
        plano.Impressao.Should().Be(Gravacao.Impressao(caminho));
        plano.Titulo.Should().Be("Sugerir 2 alterações neste documento?");
        plano.Consequencia.Should().StartWith("As alterações entram como revisões no Word, em nome de Ana:");
        plano.Resumo.Should().Be("2 trechos");
        plano.Arquivo.Should().Be(Path.GetFullPath(caminho));
    }

    [Fact]
    public void AListaInserida_ViraItem()
    {
        string caminho = Relatorio();

        EditorDeDocx.Planejar(caminho, Json("""[{"insert_after":3,"text":"RH","style":"List"}]""")).Gravar();

        LeitorDeDocumento.Abrir(caminho).Blocos[3].Should().Match<Bloco>(b => b.Tipo == TipoDeBloco.Item && b.Texto == "RH");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Criação
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ACriacao_ComTitulosListaETabela_SeReleComoFoiPedida()
    {
        string caminho = Path.Combine(_pasta, "novo.docx");
        var conteudo = Json("""
            {"blocks":[
              {"style":"Heading1","text":"Escopo"},
              {"text":"Texto normal."},
              {"list":["um","dois"]},
              {"style":"Heading2","text":"Ramais"},
              {"table":[["Setor","Ramal"],["TI",2101]]}
            ]}
            """);

        var g = EditorDeDocx.PlanejarCriacao(caminho, conteudo);
        g.Gravar();

        g.Plano.Criar.Should().BeTrue();
        g.Plano.Titulo.Should().Be("Criar este documento?");
        g.Plano.Resumo.Should().Be("5 blocos · 1 tabela");
        g.Plano.Previa!.Linhas.Select(l => l.Single()).Should().Equal(
            "[Título 1] Escopo", "Texto normal.", "• um", "• dois", "[Título 2] Ramais", "[Tabela 2×2] Setor | Ramal");

        var doc = LeitorDeDocumento.Abrir(caminho);
        doc.Blocos.Select(b => (b.Tipo, b.Nivel, b.Texto)).Should().Equal(
            (TipoDeBloco.Titulo, 1, "Escopo"),
            (TipoDeBloco.Paragrafo, 0, "Texto normal."),
            (TipoDeBloco.Item, 1, "um"),
            (TipoDeBloco.Item, 1, "dois"),
            (TipoDeBloco.Titulo, 2, "Ramais"),
            (TipoDeBloco.Tabela, 0, ""));
        doc.Blocos[^1].Tabela![1].Should().Equal("TI", "2101");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Controle de alterações
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// "Aceitar todas" / "Rejeitar todas" do Word, no essencial: o que sai some, o que fica perde
    /// a marca, e a marca de parágrafo que sai junta o parágrafo ao seguinte.
    /// </summary>
    private static void Revisar(string caminho, bool aceitar)
    {
        using var doc = WordprocessingDocument.Open(caminho, true);
        var body = doc.MainDocumentPart!.Document!.Body!;

        foreach (var tr in body.Descendants<TableRow>().ToList())
            if (tr.GetFirstChild<TableRowProperties>()?.GetFirstChild<Deleted>() is { } d)
            {
                if (aceitar) tr.Remove();
                else d.Remove();
            }

        var sai = aceitar ? body.Descendants<DeletedRun>().Cast<OpenXmlElement>() : body.Descendants<InsertedRun>();
        foreach (var x in sai.ToList()) x.Remove();

        var fica = aceitar ? body.Descendants<InsertedRun>().Cast<OpenXmlElement>() : body.Descendants<DeletedRun>();
        foreach (var x in fica.ToList())
        {
            foreach (var c in x.ChildElements.ToList())
            {
                c.Remove();
                x.InsertBeforeSelf(c);
            }
            x.Remove();
        }
        if (!aceitar)
            foreach (var t in body.Descendants<DeletedText>().ToList())
            {
                t.InsertBeforeSelf(new Text(t.Text) { Space = SpaceProcessingModeValues.Preserve });
                t.Remove();
            }

        foreach (var p in body.Descendants<Paragraph>().ToList())
        {
            var m = p.ParagraphProperties?.ParagraphMarkRunProperties;
            OpenXmlElement? marcaSai = aceitar ? m?.GetFirstChild<Deleted>() : m?.GetFirstChild<Inserted>();
            OpenXmlElement? marcaFica = aceitar ? m?.GetFirstChild<Inserted>() : m?.GetFirstChild<Deleted>();
            marcaFica?.Remove();
            if (marcaSai == null) continue;
            marcaSai.Remove();

            if (p.NextSibling() is Paragraph seguinte)
            {
                OpenXmlElement? ancora = seguinte.ParagraphProperties;
                foreach (var c in p.ChildElements.Where(c => c is not ParagraphProperties).ToList())
                {
                    c.Remove();
                    if (ancora == null) seguinte.PrependChild(c);
                    else ancora.InsertAfterSelf(c);
                    ancora = c;
                }
                p.Remove();
            }
            else if (p.InnerText.Length == 0) p.Remove();
        }
    }

    private static string[] Textos(string caminho) =>
        LeitorDeDocumento.Abrir(caminho).Blocos
            .Select(b => b.Tabela == null ? b.Texto : string.Join(" ; ", b.Tabela.Select(l => string.Join("|", l))))
            .ToArray();

    private string Copia(string caminho, string nome)
    {
        string destino = Path.Combine(_pasta, nome);
        File.Copy(caminho, destino);
        return destino;
    }

    [Fact]
    public void AceitarTudo_DaOPedido_ERejeitarTudo_DevolveOOriginal()
    {
        string caminho = Relatorio();
        var original = Textos(caminho);

        EditorDeDocx.Planejar(caminho, Json("""
            [{"replace":"março","with":"abril"},{"insert_after":1,"text":"Novo"},{"delete":2},
             {"table":1,"row":2,"col":2,"value":"2109"}]
            """), "Ana").Gravar();
        var esperado = new[] { "Escopo", "Novo", "Financeiro", "Setor|Ramal ; TI|2109", "Prazos", "A entrega é em abril." };
        Textos(caminho).Should().Equal(esperado);

        string aceito = Copia(caminho, "aceito.docx"), rejeitado = Copia(caminho, "rejeitado.docx");
        Revisar(aceito, aceitar: true);
        Revisar(rejeitado, aceitar: false);

        Textos(aceito).Should().Equal(esperado);
        Textos(rejeitado).Should().Equal(original);
    }

    [Fact]
    public void InsertEDelete_MarcamAMarcaDeParagrafo()
    {
        string caminho = Relatorio();

        EditorDeDocx.Planejar(caminho, Json("""[{"insert_after":1,"text":"Novo"},{"delete":2}]"""), "Ana").Gravar();

        using var doc = WordprocessingDocument.Open(caminho, false);
        var ps = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().ToList();
        var novo = ps.Single(p => p.InnerText == "Novo");
        novo.ParagraphProperties!.ParagraphMarkRunProperties!.GetFirstChild<Inserted>()!.Author!.Value.Should().Be("Ana");
        novo.Descendants<InsertedRun>().Should().ContainSingle();
        var apagado = ps.Single(p => p.Descendants<DeletedText>().Any(t => t.Text.Contains("três setores")));
        apagado.ParagraphProperties!.ParagraphMarkRunProperties!.GetFirstChild<Deleted>().Should().NotBeNull();
        apagado.Descendants<Text>().Should().BeEmpty();
    }

    [Fact]
    public void ATabelaApagada_TemCadaLinhaMarcada_ESomeDaLeitura()
    {
        string caminho = Relatorio();

        EditorDeDocx.Planejar(caminho, Json("""[{"delete":4}]"""), "Ana").Gravar();

        using (var doc = WordprocessingDocument.Open(caminho, false))
            doc.MainDocumentPart!.Document!.Body!.Descendants<TableRow>()
               .Should().OnlyContain(r => r.TableRowProperties!.GetFirstChild<Deleted>() != null && !r.Descendants<Text>().Any());
        Textos(caminho).Should().Equal("Escopo", "O projeto atende três setores.", "Financeiro", "Prazos", "A entrega é em março.");

        Revisar(caminho, aceitar: false);
        Textos(caminho).Should().Contain("Setor|Ramal ; TI|2101");
    }

    [Fact]
    public void ARevisaoDeOutroAutor_FicaIntacta_EOsIdsNaoSeRepetem()
    {
        string caminho = Docx("bruno.docx", new Paragraph(
            R("Valor: "),
            new DeletedRun(new Run(new DeletedText("100"))) { Id = "7", Author = "Bruno" },
            new InsertedRun(R("200")) { Id = "8", Author = "Bruno" },
            R(" reais por mês")));

        EditorDeDocx.Planejar(caminho, Json("""[{"replace":"reais","with":"dólares"}]"""), "Ana").Gravar();

        using var doc = WordprocessingDocument.Open(caminho, false);
        var body = doc.MainDocumentPart!.Document!.Body!;
        var bruno = body.Descendants().Where(e => e is DeletedRun or InsertedRun)
                        .Where(e => e.GetAttribute("author", e.NamespaceUri).Value == "Bruno").ToList();
        bruno.Select(e => e.InnerText).Should().Equal("100", "200");
        var ids = body.Descendants().Where(e => e is DeletedRun or InsertedRun).Select(e => e.GetAttribute("id", e.NamespaceUri).Value).ToList();
        ids.Should().OnlyHaveUniqueItems();
        LeitorDeDocumento.Abrir(caminho).Blocos.Single().Texto.Should().Be("Valor: 200 dólares por mês");
    }

    [Fact]
    public void OReplaceDentroDaInsercaoDeOutroAutor_ReleCerto_EPassaNoValidador()
    {
        string caminho = Docx("dentro.docx", new Paragraph(
            R("Valor: "), new InsertedRun(R("200 reais")) { Id = "3", Author = "Bruno" }, R(" por mês")));

        EditorDeDocx.Planejar(caminho, Json("""[{"replace":"200","with":"250"}]"""), "Ana").Gravar();

        LeitorDeDocumento.Abrir(caminho).Blocos.Single().Texto.Should().Be("Valor: 250 reais por mês");
        using var doc = WordprocessingDocument.Open(caminho, false);
        new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(doc).Select(e => e.Description).Should().BeEmpty();
    }

    [Fact]
    public void OReplace_NaoCasaTextoJaApagadoEmRevisao()
    {
        // "100" está num w:del de outro autor: não aparece no documento, não pode ser trocado.
        string caminho = Docx("apagado.docx", new Paragraph(
            R("Valor: "), new DeletedRun(new Run(new DeletedText("100"))) { Id = "1", Author = "Bruno" }, R("200")));

        var acao = () => EditorDeDocx.Planejar(caminho, Json("""[{"replace":"100","with":"300"}]"""));

        acao.Should().Throw<ErroDeDocumento>().WithMessage("*não achei*");
    }

    [Fact]
    public void ADocumentoEditadoComRevisoes_PassaNoValidadorDoOpenXml()
    {
        string caminho = Relatorio();

        EditorDeDocx.Planejar(caminho, Json("""
            [{"replace":"três setores","with":"quatro"},{"insert_after":3,"text":"RH","style":"List"},
             {"delete":4},{"delete":6},{"insert_after":6,"text":"Fim","style":"Heading2"}]
            """), "Ana").Gravar();

        using var doc = WordprocessingDocument.Open(caminho, false);
        new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(doc).Select(e => e.Description).Should().BeEmpty();
    }

    [Fact]
    public void ODocumentoCriadoEEditado_PassaNoValidadorDoOpenXml()
    {
        // XML fora da ordem do esquema (abstractNum depois de num, estilo sem tipo) faz o Word
        // dizer "conteúdo ilegível" ao abrir; o validador pega isso antes do usuário.
        string caminho = Path.Combine(_pasta, "valido.docx");
        EditorDeDocx.PlanejarCriacao(caminho, Json("""
            {"blocks":[{"style":"Heading1","text":"A"},{"list":["b"]},{"table":[["c","d"],["e"]]}]}
            """)).Gravar();
        EditorDeDocx.Planejar(caminho, Json("""[{"insert_after":1,"text":"x","style":"Heading3"},{"table":1,"row":2,"col":2,"value":"f"}]""")).Gravar();

        using var doc = WordprocessingDocument.Open(caminho, false);
        new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(doc).Select(e => e.Description).Should().BeEmpty();
    }

    [Fact]
    public void ACriacaoSobreArquivoExistente_SubstituiComCopia()
    {
        string caminho = Relatorio();

        var plano = EditorDeDocx.PlanejarCriacao(caminho, Json("""{"blocks":[{"text":"Só isto."}]}""")).Plano;

        plano.Criar.Should().BeFalse();
        plano.Titulo.Should().Be("Substituir este documento inteiro?");
        plano.Copia.Should().NotBeNull();
        plano.Impressao.Should().NotBeNull();
    }

    [Fact]
    public void ACriacaoSemBlocks_DaErro()
    {
        var acao = () => EditorDeDocx.PlanejarCriacao(Path.Combine(_pasta, "x.docx"), Json("""{"text":"oi"}"""));

        acao.Should().Throw<ErroDeDocumento>().WithMessage("*blocks*");
    }
}
