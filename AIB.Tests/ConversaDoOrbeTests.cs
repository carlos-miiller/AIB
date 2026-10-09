using System;
using System.Threading.Tasks;
using AIB.Services;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A conversa do orbe se mantém leve pela compactação por pausa. Pedido: "temos de pensar
    /// em uma forma de ir limpando ela dinamicamente sem eliminar ela".
    /// </summary>
    public class ConversaDoOrbeTests
    {
        private static readonly DateTime Agora = new(2026, 10, 6, 15, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void ParadaHaDuasHoras_ComAlgoNovo_Compacta()
        {
            ConversaDoOrbe.DeveCompactar(true, false, Agora.AddHours(-2), Agora).Should().BeTrue();
        }

        [Theory]
        [InlineData(false, false, -3, "nada novo desde a última: cada batida ouviria \"nada a compactar\"")]
        [InlineData(true, true, -3, "turno rodando")]
        [InlineData(true, false, -1, "parada há pouco")]
        public void NaoCompacta(bool algoNovo, bool ocupada, int horas, string porque)
        {
            ConversaDoOrbe.DeveCompactar(algoNovo, ocupada, Agora.AddHours(horas), Agora).Should().BeFalse(porque);
        }

        [Fact]
        public void SemAtividade_NaoCompacta()
        {
            ConversaDoOrbe.DeveCompactar(true, false, null, Agora).Should().BeFalse();
        }

        // Pedido: "no modo shadow, quero mandar a informação de dia da semana - DD/MM/YYYY |
        // hh:mm ... quero dar à Ellen o senso de tempo".
        private static readonly DateTime Sexta = new(2026, 10, 9, 14, 32, 0);

        [Fact]
        public void OCarimbo_TemDiaDaSemanaDataEHora_ESaiInteiro()
        {
            string fala = ConversaDoOrbe.Carimbar("lembra do ramal?", Sexta);

            fala.Should().Be("[sexta-feira - 09/10/2026 | 14:32]\nlembra do ramal?");
            ConversaDoOrbe.SemCarimbo(fala).Should().Be("lembra do ramal?");

            // Só o carimbo do começo sai; colchete digitado pelo usuário fica.
            ConversaDoOrbe.SemCarimbo("[urgente] liga pro Fernando").Should().Be("[urgente] liga pro Fernando");
            ConversaDoOrbe.SemCarimbo(ConversaDoOrbe.Carimbar(fala, Sexta)).Should().Be(fala);
        }

        [Fact]
        public async Task AFalaDoUsuario_VaiAoModeloComOCarimbo_EABarraFicaSemEle()
        {
            var servico = JanelaDeEnsaio.Servico();
            var doOrbe = new ConversaDoOrbe(JanelaDeEnsaio.Conversa(servico), servico) { Agora = () => Sexta };

            string? ouvido = null;
            doOrbe.UsuarioFalou += texto => ouvido = texto;

            await doOrbe.EnviarAsync("que horas são?");

            var falas = doOrbe.Conversa.UltimasFalas(10, int.MaxValue);
            falas.Should().Contain((true, "[sexta-feira - 09/10/2026 | 14:32]\nque horas são?"),
                "é o que o modelo recebe e o que fica no histórico");
            ouvido.Should().Be("que horas são?", "o resto do programa conta as palavras que o usuário digitou");
        }

        [Theory]
        [InlineData("/compact", ConversaDoOrbe.Comando.Compactar)]
        [InlineData("  /COMPACT ", ConversaDoOrbe.Comando.Compactar)]
        [InlineData("/memoria", ConversaDoOrbe.Comando.Memoria)]
        [InlineData("/memória", ConversaDoOrbe.Comando.Memoria)]
        [InlineData("/memory", ConversaDoOrbe.Comando.Memoria)]
        [InlineData("/compact agora", ConversaDoOrbe.Comando.Nenhum)]
        [InlineData("o que é /compact?", ConversaDoOrbe.Comando.Nenhum)]
        [InlineData("", ConversaDoOrbe.Comando.Nenhum)]
        public void ReconheceOsComandosDeBarra(string texto, ConversaDoOrbe.Comando esperado)
        {
            ConversaDoOrbe.ComandoDe(texto).Should().Be(esperado);
        }

        [Theory]
        [InlineData("/memory")]
        [InlineData("/compact")]
        public async Task OComando_RespondeNaBarra_SemIrAoModeloNemContarComoConversa(string comando)
        {
            // Visto no uso: no orbe o /compact ia ao modelo como texto comum. Os comandos de
            // barra só existiam na janela de chat, e lá agem sobre a conversa da janela.
            var servico = JanelaDeEnsaio.Servico();
            var doOrbe = new ConversaDoOrbe(JanelaDeEnsaio.Conversa(servico), servico);
            int antes = doOrbe.Conversa.SnapshotHistory().Count;

            string? resposta = null;
            bool falou = false;
            doOrbe.Respondeu += texto => resposta = texto;
            doOrbe.UsuarioFalou += _ => falou = true;

            await doOrbe.EnviarAsync(comando);

            resposta.Should().NotBeNullOrWhiteSpace();
            if (comando == "/memory") resposta.Should().StartWith("```", "a conta é alinhada por espaços");

            doOrbe.Conversa.SnapshotHistory().Count.Should().Be(antes, "o comando não entra na conversa");
            falou.Should().BeFalse("comando não é conversa: o afeto não anda");
            doOrbe.Ocupada.Should().BeFalse();
            doOrbe.UltimaAtividadeUtc.Should().BeNull();
        }

        [Theory]
        [InlineData("ok", 1)]
        [InlineData("  sim, resolvi ontem à noite  ", 5)]
        [InlineData("", 0)]
        public void ContaPalavras(string texto, int palavras)
        {
            ConversaDoOrbe.Palavras(texto).Should().Be(palavras);
        }
    }
}
