using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using AIB.Ui;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// As falas da IA no orbe em Markdown. Visto no uso: a mesma resposta saía formatada na
    /// janela de chat e, no orbe, crua — asteriscos, cercas de código e marcadores à mostra.
    /// </summary>
    public class TextoDaIATests
    {
        private const string Resposta =
            "Pronto! A **Retirada #38** foi aprovada:\n\n* Dell Optiplex `CPS-DTP-5752`\n* Monitor 19\"";

        private static IEnumerable<T> Descendentes<T>(DependencyObject raiz) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(raiz); i++)
            {
                var filho = VisualTreeHelper.GetChild(raiz, i);
                if (filho is T achado) yield return achado;
                foreach (var neto in Descendentes<T>(filho)) yield return neto;
            }
        }

        private static IEnumerable<Inline> Inlines(Block bloco) => bloco switch
        {
            Paragraph p => p.Inlines.SelectMany(Achatar),
            List l => l.ListItems.SelectMany(i => i.Blocks).SelectMany(Inlines),
            Section s => s.Blocks.SelectMany(Inlines),
            _ => Enumerable.Empty<Inline>()
        };

        private static IEnumerable<Inline> Achatar(Inline inline) =>
            inline is Span span ? new[] { inline }.Concat(span.Inlines.SelectMany(Achatar)) : new[] { inline };

        private static void ConferirFormatado(FlowDocument documento)
        {
            string texto = new TextRange(documento.ContentStart, documento.ContentEnd).Text;

            texto.Should().Contain("Retirada #38").And.Contain("CPS-DTP-5752");
            texto.Should().NotContain("**", "negrito é formato, não asterisco").And.NotContain("`");

            documento.Blocks.OfType<List>().Should().ContainSingle("os marcadores viram lista")
                .Which.ListItems.Should().HaveCount(2);
            documento.Blocks.SelectMany(Inlines).Should().Contain(
                i => i.FontWeight == FontWeights.Bold || i is Bold, "o trecho entre asteriscos sai em negrito");
        }

        [Fact]
        public void OControle_DesenhaOMarkdown_ETrocaQuandoOTextoMuda()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var controle = new TextoDaIA { Texto = Resposta };
                var janela = new Window { Content = controle, Width = 500, Height = 300, ShowActivated = false };
                janela.Show();
                janela.UpdateLayout();

                controle.Visor.Should().NotBeNull();
                ConferirFormatado(controle.Visor!.Document);

                controle.Texto = "só isto";
                new TextRange(controle.Visor.Document.ContentStart, controle.Visor.Document.ContentEnd).Text
                    .Trim().Should().Be("só isto");

                janela.Close();
            });
        }

        [Fact]
        public void AFalaNoOrbe_SaiFormatada_ComoNaJanelaDeChat()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var orbe = new ShadowAssistantWindow();
                orbe.AbrirBarra();
                orbe.MostrarFala(Resposta);
                orbe.Show();
                orbe.UpdateLayout();

                var fala = Descendentes<TextoDaIA>(orbe).Should().ContainSingle().Subject;
                fala.Visor.Should().NotBeNull();
                ConferirFormatado(fala.Visor!.Document);

                // O texto cru continua sendo o que o orbe guarda: é ele que vai para o histórico.
                orbe.TextoDaFala.Should().Be(Resposta);

                orbe.Close();
            });
        }
    }
}
