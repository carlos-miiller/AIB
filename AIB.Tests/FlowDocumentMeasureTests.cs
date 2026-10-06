using System;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using AIB.Ui;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A medição do documento por fora, que é o que faz a bolha da IA encolher até o texto.
    /// <para>
    /// Vale um ensaio próprio porque o defeito que ela corrige não aparece como erro: um
    /// <see cref="FlowDocument"/> devolve margem <c>Auto</c> — isto é, <see cref="double.NaN"/>
    /// — e NaN atravessa toda a soma em silêncio. A bolha simplesmente voltava a ocupar a
    /// largura inteira, sem exceção nem log.
    /// </para>
    /// </summary>
    public class FlowDocumentMeasureTests
    {
        /// <summary>FlowDocument e FormattedText exigem STA.</summary>
        private static T EmSta<T>(Func<T> f)
        {
            T resultado = default!;
            Exception? falha = null;

            var t = new Thread(() =>
            {
                try { resultado = f(); }
                catch (Exception ex) { falha = ex; }
            });

            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();

            if (falha != null) throw falha;
            return resultado;
        }

        private static FlowDocument Documento(params string[] paragrafos)
        {
            var doc = new FlowDocument { FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 14 };
            foreach (var texto in paragrafos)
                doc.Blocks.Add(new Paragraph(new Run(texto)));
            return doc;
        }

        [Fact]
        public void TextoCurto_MedeMenosQueTextoLongo()
        {
            var (curto, longo) = EmSta(() => (
                FlowDocumentMeasure.LarguraNatural(Documento("Olá.")),
                FlowDocumentMeasure.LarguraNatural(Documento(
                    "Uma frase bem mais comprida, que ocuparia várias linhas se fosse quebrada."))));

            curto.Should().BeGreaterThan(0);
            longo.Should().BeGreaterThan(curto * 2,
                "a medição precisa distinguir conteúdos de tamanhos diferentes");
        }

        [Fact]
        public void MargemAutomatica_NaoContaminaAContaComNaN()
        {
            // O defeito real. Block.Margin num FlowDocument nasce Auto, que o WPF guarda como
            // NaN; somar isso devolvia NaN para o documento inteiro e a bolha voltava a ocupar
            // a largura toda, sem nenhum erro visível.
            double largura = EmSta(() =>
            {
                var doc = Documento("Kai online. Olá.");
                var paragrafo = (Paragraph)doc.Blocks.FirstBlock;

                // Documenta o valor que o WPF entrega de fato — é ele que quebrava a conta.
                (double.IsNaN(paragrafo.Margin.Left) || paragrafo.Margin.Left == 0)
                    .Should().BeTrue("este ensaio só tem sentido enquanto a margem puder vir Auto");

                return FlowDocumentMeasure.LarguraNatural(doc);
            });

            double.IsNaN(largura).Should().BeFalse("NaN some com a bolha em silêncio");
            largura.Should().BeGreaterThan(0);
        }

        [Fact]
        public void ParagrafoMaisLargo_DefineALarguraDoDocumento()
        {
            double largura = EmSta(() => FlowDocumentMeasure.LarguraNatural(
                Documento("curto", "um parágrafo consideravelmente mais largo que o outro")));

            double soDoLargo = EmSta(() => FlowDocumentMeasure.LarguraNatural(
                Documento("um parágrafo consideravelmente mais largo que o outro")));

            largura.Should().BeApproximately(soDoLargo, 0.5);
        }

        [Fact]
        public void QuebraDeLinha_ReiniciaAContagem()
        {
            // Duas linhas curtas não são uma linha longa: o documento é tão largo quanto a
            // maior delas.
            double comQuebra = EmSta(() =>
            {
                var doc = new FlowDocument { FontSize = 14 };
                var p = new Paragraph();
                p.Inlines.Add(new Run("primeira parte"));
                p.Inlines.Add(new LineBreak());
                p.Inlines.Add(new Run("segunda parte"));
                doc.Blocks.Add(p);
                return FlowDocumentMeasure.LarguraNatural(doc);
            });

            double emUmaLinha = EmSta(() => FlowDocumentMeasure.LarguraNatural(
                Documento("primeira partesegunda parte")));

            comQuebra.Should().BeLessThan(emUmaLinha);
        }

        [Fact]
        public void TrechoEmNegrito_EntraNaSomaDaLinha()
        {
            // Bold é um Span: o conteúdo dele é desenhado na mesma linha e precisa somar.
            double comNegrito = EmSta(() =>
            {
                var doc = new FlowDocument { FontSize = 14 };
                var p = new Paragraph();
                p.Inlines.Add(new Run("antes "));
                p.Inlines.Add(new Bold(new Run("destaque")));
                p.Inlines.Add(new Run(" depois"));
                doc.Blocks.Add(p);
                return FlowDocumentMeasure.LarguraNatural(doc);
            });

            double soAsPontas = EmSta(() => FlowDocumentMeasure.LarguraNatural(
                Documento("antes  depois")));

            comNegrito.Should().BeGreaterThan(soAsPontas,
                "o texto dentro do Bold não pode ser ignorado");
        }

        [Fact]
        public void DocumentoNulo_OuVazio_NaoExplode()
        {
            EmSta(() => FlowDocumentMeasure.LarguraNatural(null)).Should().Be(0);
            EmSta(() => FlowDocumentMeasure.LarguraNatural(new FlowDocument())).Should().Be(0);
        }
    }
}
