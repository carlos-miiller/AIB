using AIB.Ui;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O interpretador de Markdown das bolhas da IA.
    /// <para>
    /// A regra do Markdown é que quebra de linha simples não quebra linha na saída: só linha em
    /// branco separa parágrafo. Correto para documento escrito à mão, errado para conversa — o
    /// modelo escreve com quebras simples e a resposta chegava na tela como um bloco corrido.
    /// </para>
    /// <para>
    /// O ensaio usa o renderizador de HTML do Markdig, e não o de WPF: o que está sendo travado
    /// é a CONFIGURAÇÃO do pipeline, e em HTML a quebra tem um nome que dá para afirmar.
    /// </para>
    /// </summary>
    public class MarkdownPipelineTests
    {
        private static string Html(string markdown) =>
            Markdig.Markdown.ToHtml(markdown, MarkdownPipelines.Conversa);

        [Fact]
        public void QuebraSimples_ViraQuebraDeVerdade()
        {
            Html("Li o arquivo.\nFaltam dois itens.")
                .Should().Contain("<br");
        }

        [Fact]
        public void ParagrafosSeparados_ContinuamSendoDois()
        {
            var html = Html("Primeiro.\n\nSegundo.");

            html.Should().Contain("<p>Primeiro.</p>");
            html.Should().Contain("<p>Segundo.</p>");
        }

        [Fact]
        public void ListaEBlocoDeCodigo_ContinuamFuncionando()
        {
            // A quebra rígida não pode atrapalhar a estrutura: uma lista cujos itens virassem
            // <br> dentro de um parágrafo perderia os marcadores.
            Html("- um\n- dois").Should().Contain("<li>");
            Html("```\ncodigo\n```").Should().Contain("<code");
        }

        [Fact]
        public void Tabela_ContinuaSendoTabela()
        {
            // Vem das extensões suportadas pelo Markdig.Wpf. Construir o pipeline à mão sem
            // elas silenciaria a tabela, que apareceria como texto com barras verticais.
            Html("| a | b |\n| - | - |\n| 1 | 2 |").Should().Contain("<table");
        }
    }
}
