using System;
using System.Linq;
using AIB.Services.Mail;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O agrupamento por conversa e a linha de metadados de <c>tela-chat-v3.html §3.10</c>.
    /// <para>
    /// Nenhum dos três campos vem pronto do servidor. O IMAP entrega mensagens soltas;
    /// "3 respostas" e "aguardando retorno" são leitura nossa por cima delas — e é por isso que
    /// isto é código, e não um campo a mais no transporte.
    /// </para>
    /// </summary>
    public class ConversaDeEmailTests
    {
        private static MensagemDeEmail Msg(
            uint uid, string thread, string de, DateTime quando, string assunto = "assunto") =>
            new(uid, thread, de, "Nome", assunto, quando, true, false,
                Array.Empty<string>(), true, "corpo");

        private static readonly DateTime Agora = new(2026, 9, 10, 14, 0, 0, DateTimeKind.Local);

        // ─────────────────────────────────────────────────────────────────────
        // Agrupar
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void MesmaThread_VIRA_UmItemSo()
        {
            // Cinco respostas eram cinco linhas repetindo o mesmo assunto. Mostrar tudo o que se
            // leu é o oposto de triar.
            var conversas = ConversaDeEmail.Agrupar(new[]
            {
                Msg(1, "T1", "cliente@x.com", Agora.AddHours(-5)),
                Msg(2, "T1", "eu@casa.com", Agora.AddHours(-3)),
                Msg(3, "T1", "cliente@x.com", Agora.AddHours(-1))
            });

            conversas.Should().HaveCount(1);
            conversas[0].Mensagens.Should().Be(3);
        }

        [Fact]
        public void AConversa_TRAZ_AMensagemMaisRECENTE()
        {
            // Uma conversa de cinco dias mostrando a data de abertura diria que nada aconteceu
            // desde então.
            var conversas = ConversaDeEmail.Agrupar(new[]
            {
                Msg(1, "T1", "a@x.com", Agora.AddDays(-5), "primeiro"),
                Msg(2, "T1", "a@x.com", Agora.AddMinutes(-10), "ultimo")
            });

            conversas[0].Recente.Assunto.Should().Be("ultimo");
        }

        [Fact]
        public void SemThreadId_CadaMensagem_EhAPropriaConversa()
        {
            // Provedor que não é Gmail não expõe X-GM-THRID. Juntar todas as sem-thread numa só
            // grudaria assuntos que nada têm a ver — errar para MENOS é o lado seguro.
            var conversas = ConversaDeEmail.Agrupar(new[]
            {
                Msg(1, "", "a@x.com", Agora),
                Msg(2, "", "b@x.com", Agora)
            });

            conversas.Should().HaveCount(2);
            conversas.Should().OnlyContain(c => c.Mensagens == 1);
        }

        [Fact]
        public void ThreadsDeCONTAS_Diferentes_NaoSeMisturam()
        {
            var a = new MensagemDeEmail(1, "T1", "x@x.com", "N", "a", Agora, true, false,
                Array.Empty<string>(), true, "c", false, "conta1@x.com");
            var b = new MensagemDeEmail(1, "T1", "x@x.com", "N", "b", Agora, true, false,
                Array.Empty<string>(), true, "c", false, "conta2@x.com");

            ConversaDeEmail.Agrupar(new[] { a, b }).Should().HaveCount(2);
        }

        // ─────────────────────────────────────────────────────────────────────
        // De quem é a vez
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void SeVoceEscreveuPorULTIMO_AVezEhDoOutro()
        {
            var conversas = ConversaDeEmail.Agrupar(
                new[]
                {
                    Msg(1, "T1", "cliente@x.com", Agora.AddHours(-2)),
                    Msg(2, "T1", "Carlo <eu@casa.com>", Agora.AddHours(-1))
                },
                new[] { "eu@casa.com" });

            conversas[0].EsperandoVoce.Should().BeFalse();
            ConversaDeEmail.DeQuemEhAVez(false).Should().Be("Aguardando retorno");
        }

        [Fact]
        public void SeOOUTRO_EscreveuPorUltimo_AVezEhSua()
        {
            var conversas = ConversaDeEmail.Agrupar(
                new[]
                {
                    Msg(1, "T1", "eu@casa.com", Agora.AddHours(-2)),
                    Msg(2, "T1", "cliente@x.com", Agora.AddHours(-1))
                },
                new[] { "eu@casa.com" });

            conversas[0].EsperandoVoce.Should().BeTrue();
            ConversaDeEmail.DeQuemEhAVez(true).Should().Be("Nova mensagem");
        }

        [Fact]
        public void SemOsEnderecosDoUsuario_TudoVira_NovaMensagem()
        {
            // Inclusive o que você acabou de responder. É o motivo de a lista de endereços não
            // ser opcional na prática.
            var conversas = ConversaDeEmail.Agrupar(new[] { Msg(1, "T1", "eu@casa.com", Agora) });

            conversas[0].EsperandoVoce.Should().BeTrue();
        }

        [Theory]
        [InlineData("Fulano <f@x.com>", "f@x.com")]
        [InlineData("  f@x.com  ", "f@x.com")]
        [InlineData("\"Nome, Com Virgula\" <n@x.com>", "n@x.com")]
        [InlineData("", "")]
        public void OEndereco_SaiDeDentro_DoCampoDe(string campo, string esperado)
        {
            // Sem isto a comparação falharia sempre que o servidor mandasse o nome junto — que é
            // quase sempre — e toda conversa apareceria como "nova mensagem".
            ConversaDeEmail.Endereco(campo).Should().Be(esperado);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Como a data é escrita
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Hoje_EOntem_TemNomePROPRIO()
        {
            ConversaDeEmail.Quando(new DateTime(2026, 9, 10, 9, 12, 0), Agora)
                .Should().Be("Hoje · 10/09/2026, 09:12");

            ConversaDeEmail.Quando(new DateTime(2026, 9, 9, 17, 40, 0), Agora)
                .Should().Be("Ontem · 09/09/2026, 17:40");
        }

        [Fact]
        public void AteSeteDias_OhDiaDaSemana_VemNaFrente()
        {
            // 07/09/2026 é uma segunda-feira.
            ConversaDeEmail.Quando(new DateTime(2026, 9, 7, 6, 0, 0), Agora)
                .Should().StartWith("Segunda-feira · ");
        }

        [Fact]
        public void MaisVelhoQueUmaSemana_FicaSO_ADataCompleta()
        {
            // "há 2 dias" não se compara com o que o webmail mostra; a data, sim.
            ConversaDeEmail.Quando(new DateTime(2026, 3, 12, 14, 20, 0), Agora)
                .Should().Be("12/03/2026, 14:20");
        }

        [Theory]
        [InlineData(1, "1 mensagem")]
        [InlineData(2, "2 respostas")]
        [InlineData(9, "9 respostas")]
        public void OTamanhoDaConversa_EhDito_EmDuasFormas(int n, string esperado) =>
            ConversaDeEmail.Tamanho(n).Should().Be(esperado);
    }
}
