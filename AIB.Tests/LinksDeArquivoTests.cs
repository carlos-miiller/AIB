using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Documents;
using AIB.Ui;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Caminho citado na fala vira link para o Explorer. Pedido do usuário (29/10/2026): a Ellen
    /// cita o arquivo que acabou de editar ou mover, e para chegar até ele era preciso copiar o
    /// caminho e colar no Explorer.
    /// </summary>
    public class LinksDeArquivoTests
    {
        private static readonly string[] Nenhum = Array.Empty<string>();

        private static Func<string, bool> Disco(params string[] existentes) =>
            c => existentes.Contains(c, StringComparer.OrdinalIgnoreCase);

        [Fact]
        public void CaminhoComEspaco_SeguidoDeTexto_ViraLinkSoNoQueExiste()
        {
            // O caso da Ellen: nome com espaços, e a frase continua depois do caminho.
            const string arquivo = @"C:\Users\Carlo\Ellen\Proposta Comercial - Vouler 1.pdf";
            string texto = $"Movi {arquivo}, e o resto ficou na pasta.";

            var t = LinksDeArquivo.Achar(texto, Disco(arquivo), Nenhum).Should().ContainSingle().Subject;

            texto.Substring(t.Inicio, t.Tamanho).Should().Be(arquivo);
            t.Caminho.Should().Be(arquivo);
        }

        [Fact]
        public void DoisCaminhosNaMesmaLinha_ViramDoisLinks()
        {
            const string a = @"C:\t\a.md", b = @"C:\t\b.md";
            LinksDeArquivo.Achar($"Gravei {a} e {b}.", Disco(a, b), Nenhum).Select(t => t.Caminho)
                .Should().Equal(a, b);
        }

        [Fact]
        public void CaminhoQueNaoExiste_FicaTexto()
        {
            LinksDeArquivo.Achar(@"Salvei em C:\nao\existe.md.", Disco(), Nenhum).Should().BeEmpty();
        }

        [Fact]
        public void NomeSolto_ViraLink_QuandoEhDeUmArquivoRecente()
        {
            const string recente = @"C:\Users\Carlo\Ellen\01-incidentes\relatorio-vishing.md";
            string texto = "Atualizei o relatorio-vishing.md com o horário.";

            var t = LinksDeArquivo.Achar(texto, Disco(recente), new[] { recente }).Should().ContainSingle().Subject;
            t.Caminho.Should().Be(recente);

            // Pedaço de outro nome não casa.
            LinksDeArquivo.Achar("Veja o velho-relatorio-vishing.md.", Disco(recente), new[] { recente })
                .Should().BeEmpty();
        }

        [Fact]
        public void NomeRepetidoEntreOsRecentes_NaoViraLink()
        {
            // Dois "leia.md" em pastas diferentes: não dá para saber de qual a fala fala.
            string[] recentes = { @"C:\a\leia.md", @"C:\b\leia.md" };
            LinksDeArquivo.Achar("Abra o leia.md.", Disco(recentes), recentes).Should().BeEmpty();
        }

        [Fact]
        public void NoDocumento_OCaminhoViraHyperlink_EOTextoContinuaIgual()
        {
            string pasta = Path.Combine(Path.GetTempPath(), "aib-links-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(pasta);
            string arquivo = Path.Combine(pasta, "nota.md");
            File.WriteAllText(arquivo, "x");

            try
            {
                WpfHost.EmSta(() =>
                {
                    string frase = $"Gravei {arquivo} agora.";
                    var doc = new FlowDocument(new Paragraph(new Run(frase)));

                    LinksDeArquivo.Aplicar(doc, LinksDeArquivo.Existe, Nenhum);

                    var p = (Paragraph)doc.Blocks.FirstBlock;
                    var link = p.Inlines.OfType<Hyperlink>().Should().ContainSingle().Subject;
                    link.Tag.Should().Be(arquivo);
                    new TextRange(doc.ContentStart, doc.ContentEnd).Text.Trim().Should().Be(frase);
                });

                // Visto no uso (30/10/2026): caminho entre crases virava link azul sobre fundo
                // branco, herdado do estilo de código inline. O link fica sem fundo.
                WpfHost.EmSta(() =>
                {
                    var codigo = new System.Windows.Style();
                    codigo.Setters.Add(new System.Windows.Setter(TextElement.BackgroundProperty,
                        System.Windows.Media.Brushes.White));
                    var doc = new FlowDocument(new Paragraph(new Run(arquivo) { Style = codigo }));

                    LinksDeArquivo.Aplicar(doc, LinksDeArquivo.Existe, Nenhum);

                    var link = ((Paragraph)doc.Blocks.FirstBlock).Inlines.OfType<Hyperlink>().Single();
                    var run = link.Inlines.OfType<Run>().Single();
                    run.Background.Should().Be(System.Windows.Media.Brushes.Transparent);
                });
            }
            finally
            {
                try { Directory.Delete(pasta, true); } catch { }
            }
        }
    }
}
