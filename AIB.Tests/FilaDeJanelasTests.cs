using System.Collections.Generic;
using AIB.Services;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A fila das janelas flutuantes: uma por vez.
    /// <para>
    /// Defeito visto pelo usuário (16/10/2026): "quando AIB prompta múltiplas notificações ou
    /// confirmações ao mesmo tempo, todas são abertas, uma em cima da outra". Com várias
    /// ferramentas na mesma volta, cada confirmação abria a sua janela perto do orbe; a de baixo
    /// ficava escondida, com a ferramenta parada esperando uma resposta que ninguém via.
    /// </para>
    /// </summary>
    public class FilaDeJanelasTests
    {
        private readonly List<string> _naTela = new();
        private readonly List<string> _escondidas = new();

        private FilaDeJanelas<string> Nova() =>
            new(j => { _naTela.Add(j); _escondidas.Remove(j); }, j => { _naTela.Remove(j); _escondidas.Add(j); });

        [Fact]
        public void DuasConfirmacoes_AoMesmoTempo_SoAPrimeiraAparece()
        {
            var fila = Nova();

            fila.Entrar("confirmar shell", VezDaJanela.Pergunta);
            fila.Entrar("confirmar write", VezDaJanela.Pergunta);

            _naTela.Should().Equal("confirmar shell");
            fila.Esperando.Should().Be(1);

            fila.Saiu("confirmar shell");
            _naTela.Should().Contain("confirmar write");
            fila.Atual.Should().Be("confirmar write");
            fila.Esperando.Should().Be(0);
        }

        [Fact]
        public void APergunta_TiraOAvisoDaTela_EOAvisoVoltaDepois()
        {
            // Há uma ferramenta parada esperando a resposta: a pergunta não espera um aviso
            // ser lido.
            var fila = Nova();

            fila.Entrar("triagem", VezDaJanela.Triagem);
            fila.Entrar("confirmar", VezDaJanela.Pergunta);

            fila.Atual.Should().Be("confirmar");
            _escondidas.Should().Equal("triagem");

            fila.Saiu("confirmar");
            fila.Atual.Should().Be("triagem", "o aviso interrompido volta");
        }

        [Fact]
        public void OsAvisos_NaoSeInterrompem_MasARajadaFuraAFila()
        {
            var fila = Nova();

            fila.Entrar("lembrete", VezDaJanela.Lembrete);
            fila.Entrar("triagem", VezDaJanela.Triagem);
            fila.Entrar("rajada", VezDaJanela.Rajada);

            fila.Atual.Should().Be("lembrete", "aviso na tela não é interrompido por outro aviso");

            fila.Saiu("lembrete");
            fila.Atual.Should().Be("rajada");

            fila.Saiu("rajada");
            fila.Atual.Should().Be("triagem");
        }

        [Fact]
        public void AJanelaDescartadaEnquantoEspera_SaiDaFila_SemAparecer()
        {
            // A confirmação pode ser descartada (turno parado) antes da vez dela.
            var fila = Nova();

            fila.Entrar("a", VezDaJanela.Pergunta);
            fila.Entrar("b", VezDaJanela.Pergunta);
            fila.Saiu("b");

            fila.Esperando.Should().Be(0);
            fila.Saiu("a");
            fila.Atual.Should().BeNull();
            _naTela.Should().NotContain("b", "a descartada nunca chega à tela");
        }

        [Fact]
        public void AMesmaJanela_NaoEntraDuasVezes()
        {
            var fila = Nova();

            fila.Entrar("a", VezDaJanela.Pergunta);
            fila.Entrar("a", VezDaJanela.Pergunta);

            fila.Esperando.Should().Be(0);
        }
    }
}
