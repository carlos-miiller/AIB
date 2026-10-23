using System.Collections.Generic;
using System.Linq;
using AIB.Services;
using AIB.Services.Memory;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    public class ConversaNaTelaTests
    {
        private static TurnRecord Turno(params MessageRecord[] mensagens) =>
            new(0, "2026-10-15T19:25:01Z", mensagens, new List<Artifact>());

        private static MessageRecord Chamada(string id, string ferramenta, string args = "{}", string fala = "") =>
            new("assistant", fala, new List<ToolCallRecord> { new(id, ferramenta, args) });

        [Fact]
        public void TurnoQueParouNoTeto_VoltaComAsFerramentas_ENaoSoComAMarca()
        {
            // Caso real, 15/10, "Editar tarefa 3103 no Bitrix": turnos de 60 ferramentas que
            // pararam no teto, e outros cancelados, voltavam ao reabrir como UMA linha —
            // "[turno encerrado sem resposta: …]" —, porque a tela desenhava só o arquivo do
            // histórico, que guarda a última fala de cada turno.
            var turno = Turno(
                new MessageRecord("user", "edite a tarefa 3103"),
                Chamada("c1", "browser", "{\"action\":\"open\"}", "Vou abrir a tarefa."),
                new MessageRecord("tool", "SUCESSO: aberta", ToolCallId: "c1"),
                Chamada("c2", "browser", "{\"action\":\"click\"}"),
                new MessageRecord("tool", "ERRO: ref não existe", ToolCallId: "c2"),
                new MessageRecord("assistant", "[turno encerrado sem resposta: teto de 60 etapas atingido com ferramenta pendente]"));

            var itens = ConversaNaTela.Montar(new[] { turno });

            itens.Select(i => i.GetType().Name).Should().Equal("Fala", "Fala", "Acao", "Acao", "Fala");
            ((ConversaNaTela.Fala)itens[1]).Texto.Should().Be("Vou abrir a tarefa.",
                "a fala do meio da cadeia também volta, e não só a última");

            var falhou = (ConversaNaTela.Acao)itens[3];
            falhou.Ferramenta.Should().Be("browser");
            falhou.Falhou.Should().BeTrue();
            falhou.Detalhe.Should().Contain("ref não existe");

            ((ConversaNaTela.Fala)itens[4]).Texto.Should().StartWith("[turno encerrado sem resposta");
        }

        [Fact]
        public void ChamadaSemResultado_RaciocinioERememberQueFalhou_NaoAparecem()
        {
            var turno = Turno(
                new MessageRecord("user", "oi"),
                Chamada("c1", Ferramentas.Lembrar, "{\"fact\":\"gosta de café\"}"),
                new MessageRecord("tool", "ERRO: não gravou", ToolCallId: "c1"),
                Chamada("c2", "read"),
                new MessageRecord("assistant", "<think>pensando alto</think>Pronto."));

            var itens = ConversaNaTela.Montar(new[] { turno });

            itens.Should().HaveCount(2);
            ((ConversaNaTela.Fala)itens[1]).Texto.Should().Be("Pronto.");
        }

        [Fact]
        public void RememberQueDeuCerto_ViraOAviso_ENaoChip()
        {
            var turno = Turno(
                new MessageRecord("user", "lembra que gosto de café"),
                Chamada("c1", Ferramentas.Lembrar, "{\"fact\":\"gosta de café\"}"),
                new MessageRecord("tool", "SUCESSO: guardado", ToolCallId: "c1"),
                new MessageRecord("assistant", "Anotado."));

            var itens = ConversaNaTela.Montar(new[] { turno });

            itens[1].Should().BeOfType<ConversaNaTela.Lembrou>();
            itens.OfType<ConversaNaTela.Acao>().Should().BeEmpty();
        }
    }
}
