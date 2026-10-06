using System;
using System.IO;
using AIB.Services;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A persona puxando assunto pelo orbe. Pedido: "no lugar dela só responder o que mandamos,
    /// ela também ativamente mandar mensagens" — com ritmo adaptativo, sem intervalo mínimo fixo,
    /// e horário de silêncio configurável.
    /// </summary>
    public class IniciativaTests : IDisposable
    {
        private static readonly TimeSpan Dez = TimeSpan.FromHours(10);
        private static readonly TimeSpan Ini = TimeSpan.FromHours(22);
        private static readonly TimeSpan Fim = TimeSpan.FromHours(8);
        private static readonly DateTime Agora = new(2026, 10, 6, 13, 0, 0, DateTimeKind.Utc);

        private readonly string _raiz = Path.Combine(Path.GetTempPath(), "aib-iniciativa-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_raiz, true); } catch { }
        }

        private static string? Impede(EstadoDaIniciativa e, TimeSpan? hora = null, bool presente = true,
                                      bool livre = true, DateTime? conversa = null) =>
            Iniciativa.Impedimento(e, Agora, hora ?? Dez, Ini, Fim, presente, livre, conversa);

        [Theory]
        [InlineData(23, true)]
        [InlineData(3, true)]
        [InlineData(8, false)]
        [InlineData(14, false)]
        public void OSilencio_AtravessaAMeiaNoite(int hora, bool emSilencio)
        {
            Iniciativa.EmSilencio(TimeSpan.FromHours(hora), Ini, Fim).Should().Be(emSilencio);
        }

        [Fact]
        public void SilencioComInicioIgualAoFim_EstaDesligado()
        {
            Iniciativa.EmSilencio(TimeSpan.FromHours(3), Dez, Dez).Should().BeFalse();
        }

        [Fact]
        public void OEspaco_SaiDoRitmo()
        {
            // Dia acordado de 14 h (22h–8h de silêncio).
            var acordadas = Iniciativa.HorasAcordadas(Ini, Fim);
            acordadas.Should().Be(TimeSpan.FromHours(14));
            Iniciativa.Espaco(2, acordadas).Should().Be(TimeSpan.FromHours(7));
            Iniciativa.Espaco(100, acordadas).Should().Be(TimeSpan.FromHours(14) / Iniciativa.RitmoMaximo, "o ritmo tem teto");
        }

        [Fact]
        public void ComTudoLivre_Pondera()
        {
            Impede(new EstadoDaIniciativa()).Should().BeNull();
        }

        [Fact]
        public void NaoInsiste_EnquantoAUltimaEsperaResposta()
        {
            Impede(new EstadoDaIniciativa { Aguardando = true }).Should().Be("esperando resposta");
        }

        [Fact]
        public void NoSilencio_AusenteOuConversando_NaoPondera()
        {
            var e = new EstadoDaIniciativa();
            Impede(e, hora: TimeSpan.FromHours(23)).Should().Be("silêncio");
            Impede(e, presente: false).Should().Be("ausente ou ocupado");
            Impede(e, livre: false).Should().Be("turno ou conversa aberta");
            Impede(e, conversa: Agora.AddMinutes(-5)).Should().Be("conversa recente");
            Impede(e, conversa: Agora.AddHours(-1)).Should().BeNull();
        }

        [Fact]
        public void ORitmoDoDia_LimitaAsFalas()
        {
            Impede(new EstadoDaIniciativa { Ritmo = 2, FalasHoje = 2 }).Should().Be("ritmo do dia cumprido");
            Impede(new EstadoDaIniciativa { PonderacoesHoje = Iniciativa.PonderacoesPorDia }).Should().Be("teto de ponderações");
        }

        [Fact]
        public void DepoisDeFalar_EsperaOEspacoDoRitmo()
        {
            var e = new EstadoDaIniciativa { Ritmo = 2, FalasHoje = 1, UltimaFalaUtc = Agora.AddHours(-3) };
            Impede(e).Should().Be("cedo para outra fala");

            e.UltimaFalaUtc = Agora.AddHours(-8);
            Impede(e).Should().BeNull();
        }

        [Fact]
        public void DepoisDeFicarQuieta_EsperaMeioEspaco()
        {
            // Cada ponderação é paga, mesmo terminando em NADA.
            var e = new EstadoDaIniciativa { Ritmo = 2, UltimaPonderacaoUtc = Agora.AddHours(-1) };
            Impede(e).Should().Be("ponderou há pouco");
        }

        [Fact]
        public void Responder_SobeORitmo_Ignorar_Derruba()
        {
            var i = new Iniciativa(_raiz);
            double inicial = i.Estado.Ritmo;

            i.Ponderou(Agora, falou: true);
            i.Estado.Aguardando.Should().BeTrue();
            i.Respondeu();
            i.Estado.Ritmo.Should().Be(inicial + 0.5);

            i.Ponderou(Agora, falou: true);
            i.ConferirPaciencia(Agora.AddHours(1));
            i.Estado.Aguardando.Should().BeTrue("ainda dentro da paciência");
            i.ConferirPaciencia(Agora + Iniciativa.Paciencia);
            i.Estado.Aguardando.Should().BeFalse();
            i.Estado.Ritmo.Should().BeLessThan(inicial + 0.5);

            new Iniciativa(_raiz).Estado.Ritmo.Should().Be(i.Estado.Ritmo, "o ritmo sobrevive ao arranque");
        }

        [Fact]
        public void NovoDia_ZeraAsContagens()
        {
            var i = new Iniciativa(_raiz);
            i.NovoDia(new DateTime(2026, 10, 6, 9, 0, 0));
            i.Ponderou(Agora, falou: true);

            i.NovoDia(new DateTime(2026, 10, 7, 9, 0, 0));
            i.Estado.FalasHoje.Should().Be(0);
            i.Estado.PonderacoesHoje.Should().Be(0);
        }

        [Theory]
        [InlineData("NADA", true)]
        [InlineData("nada.", true)]
        [InlineData("  ", true)]
        [InlineData("Conseguiu resolver aquele servidor?", false)]
        public void NADA_EhFicarQuieta(string texto, bool quieta)
        {
            Iniciativa.EhSilencio(texto).Should().Be(quieta);
        }

        [Fact]
        public void OPedido_TemAAlmaOsFatosAsFalasEASaidaNADA()
        {
            string m = ConversationService.MaterialDaIniciativa(
                "Você é Ellen.",
                new[] { "- sobre o usuário: trabalha com TI num hospital" },
                new[] { (true, "amanhã eu testo o backup"), (false, "Combinado!") },
                new DateTime(2026, 10, 7, 10, 30, 0));

            m.Should().StartWith("Você é Ellen.")
             .And.Contain("trabalha com TI num hospital")
             .And.Contain("Usuário: amanhã eu testo o backup")
             .And.Contain("responda exatamente: NADA");
        }

        [Fact]
        public void HorarioDeSilencioIlegivel_VoltaAoPadrao()
        {
            var s = new UserAppSettings { SilencioInicio = "meia-noite", SilencioFim = "7:30" }.Sanear();
            s.SilencioInicio.Should().Be("22:00");
            s.SilencioFim.Should().Be("07:30");
        }
    }
}
