using System.Linq;
using AIB.Services;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A leitura de volta de uma conversa arquivada.
    /// <para>
    /// O arquivo de histórico guarda a conversa como texto corrido, prefixado por
    /// <c>USER:</c> e <c>AIB:</c>. Para RECUPERAR CONTEXTO o bloco inteiro basta; para ABRIR a
    /// conversa não, porque cada fala precisa voltar ao seu balão e ao seu papel no histórico
    /// do modelo. Estes ensaios cobrem esse caminho de volta.
    /// </para>
    /// </summary>
    [Collection("Historico")]
    public class ChatHistoryParseTests
    {
        [Fact]
        public void Falas_VoltamNaOrdemEComOAutorCerto()
        {
            var falas = ChatHistoryService.Parse(
                "USER: leia o AGENTS.md\n\nAIB: li, faltam dois itens\n\nUSER: quais?");

            falas.Should().HaveCount(3);
            falas[0].DoUsuario.Should().BeTrue();
            falas[0].Texto.Should().Be("leia o AGENTS.md");
            falas[1].DoUsuario.Should().BeFalse();
            falas[1].Texto.Should().Be("li, faltam dois itens");
            falas[2].DoUsuario.Should().BeTrue();
        }

        [Fact]
        public void FalaComParagrafos_ContinuaSendoUmaFalaSo()
        {
            // O motivo de varrer por linha e não por bloco separado de linha em branco:
            // resposta com markdown tem parágrafo, lista e bloco de código, e cortar nisso
            // transformaria uma resposta em cinco balões.
            var falas = ChatHistoryService.Parse(
                "AIB: primeiro parágrafo\n\nsegundo parágrafo\n\n- item\n\nUSER: ok");

            falas.Should().HaveCount(2);
            falas[0].DoUsuario.Should().BeFalse();
            falas[0].Texto.Should().Contain("primeiro parágrafo")
                .And.Contain("segundo parágrafo")
                .And.Contain("- item");
            falas[1].Texto.Should().Be("ok");
        }

        [Fact]
        public void ConteudoVazioOuSemPrefixo_NaoViraFalaNenhuma()
        {
            // Sem isto, abrir uma conversa corrompida limparia a tela e não poria nada no
            // lugar. Quem chama usa a lista vazia para desistir da troca.
            ChatHistoryService.Parse(null).Should().BeEmpty();
            ChatHistoryService.Parse("   ").Should().BeEmpty();
            ChatHistoryService.Parse("texto solto sem prefixo").Should().BeEmpty();
        }

        [Fact]
        public void FinalDeLinhaDoWindows_NaoVazaParaOTexto()
        {
            var falas = ChatHistoryService.Parse("USER: oi\r\n\r\nAIB: olá");

            falas.Should().HaveCount(2);
            falas[1].Texto.Should().Be("olá");
            falas.Should().OnlyContain(f => !f.Texto.Contains("\r"));
        }
    }
}
