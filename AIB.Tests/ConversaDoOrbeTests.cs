using System;
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
