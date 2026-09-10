using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using AIB.Services.Agent;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A prova de vida no terminal.
    /// <para>
    /// O defeito que ela cobre não é um bug de código: é uma lacuna de informação. Com o 9B,
    /// um prefill frio de alguns milhares de tokens passa de três minutos, e nesse intervalo não
    /// havia chunk, evento nem linha de log. O <c>[STREAM-END]</c> imprime quando o stream
    /// acaba e o <c>[STREAM-DBG]</c> no primeiro chunk com conteúdo — os dois chegam DEPOIS do
    /// silêncio. Por fora, era indistinguível de travamento.
    /// </para>
    /// </summary>
    public class PulsoDoTurnoTests
    {
        private sealed class Papel
        {
            private readonly List<string> _linhas = new();
            private readonly object _trava = new();

            public void Escrever(string linha)
            {
                lock (_trava) _linhas.Add(linha);
            }

            public IReadOnlyList<string> Linhas
            {
                get { lock (_trava) return _linhas.ToList(); }
            }

            public string Tudo => string.Join("\n", Linhas);

            public int Quantas(string trecho) => Linhas.Count(l => l.Contains(trecho));
        }

        private static PulsoDoTurno Novo(Papel papel, TimeSpan? intervalo = null) =>
            new(iteracao: 1,
                modelo: "qwen3.5:9b",
                tokensDoPrompt: 3485,
                reusoPrevisto: 3400,
                numCtx: 16384,
                intervalo: intervalo ?? TimeSpan.FromHours(1),
                escrever: papel.Escrever);

        // ─────────────────────────────────────────────────────────────────────
        // Abertura
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void OCabecalho_DizOQUE_EXPLICA_AEspera()
        {
            // O reuso previsto é o número que separa uma espera de segundos de uma de minutos.
            // Sem ele, as duas são visualmente idênticas até acabarem — e é justamente durante
            // a espera que o usuário precisa saber com qual está lidando.
            var papel = new Papel();
            using var pulso = Novo(papel);

            papel.Linhas.Should().HaveCount(1);
            papel.Tudo.Should().Contain("[TURNO 1]")
                .And.Contain("3485 tok")
                .And.Contain("reuso previsto 3400")
                .And.Contain("novos 85")
                .And.Contain("qwen3.5:9b")
                .And.Contain("ctx 16384");
        }

        [Fact]
        public void ReusoMaiorQueOPrompt_NaoGeraNovosNEGATIVOS()
        {
            // O previsor de prefixo trabalha com dois tokenizadores diferentes e já produziu
            // número negativo antes, na conta de cache que a UI exibia como "-0%".
            var papel = new Papel();
            using var pulso = new PulsoDoTurno(1, "m", 100, 500, 16384,
                TimeSpan.FromHours(1), papel.Escrever);

            papel.Tudo.Should().Contain("novos 0");
        }

        // ─────────────────────────────────────────────────────────────────────
        // O silêncio do prefill
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void SemNenhumToken_OPulsoDIZ_QueEstaEsperando()
        {
            var papel = new Papel();
            using var pulso = Novo(papel);

            pulso.BaterAgora();
            pulso.BaterAgora();

            papel.Quantas("aguardando o primeiro token").Should().Be(2,
                "é a linha que prova que o programa está vivo durante o prefill");
        }

        [Fact]
        public void OPrimeiroToken_EhAnunciadoUMAVEZ()
        {
            var papel = new Papel();
            using var pulso = Novo(papel);

            pulso.Escreveu(10);
            pulso.Escreveu(10);
            pulso.Raciocinou(5);

            papel.Quantas("primeiro token em").Should().Be(1,
                "o fim do prefill acontece uma vez por iteração");
        }

        [Fact]
        public void DepoisDoPrimeiroToken_OPulsoMostraOQueJaChegou()
        {
            var papel = new Papel();
            using var pulso = Novo(papel);

            pulso.Raciocinou(120);
            pulso.BaterAgora();

            papel.Tudo.Should().Contain("pensando").And.Contain("120 car");

            pulso.Escreveu(40);
            pulso.BaterAgora();

            papel.Tudo.Should().Contain("escrevendo").And.Contain("40 car");
        }

        [Fact]
        public void OPulsoRELATA_CARACTERES_ENaoTokens()
        {
            // Contar token por chunk custaria uma tokenização por token emitido — o AgentLoop
            // já amostra o contador da UI por esse motivo. Caractere é o que se sabe de graça,
            // e dizer "car" em vez de "tok" é a diferença entre informar e chutar.
            var papel = new Papel();
            using var pulso = Novo(papel);

            pulso.Escreveu(7);
            pulso.BaterAgora();

            papel.Tudo.Should().Contain("7 car").And.NotContain("7 tok");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Ferramentas
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void AFerramentaAparece_NoComecoNoMeioENoFim()
        {
            var papel = new Papel();
            using var pulso = Novo(papel);

            pulso.FerramentaComecou("read");
            pulso.BaterAgora();
            pulso.FerramentaTerminou("read", falhou: false);

            papel.Tudo.Should().Contain("ferramenta read — executando");
            papel.Tudo.Should().Contain("ferramenta read há");
            papel.Tudo.Should().Contain("ferramenta read — ok em");
        }

        [Fact]
        public void FerramentaQueFALHA_DizQueFalhou()
        {
            var papel = new Papel();
            using var pulso = Novo(papel);

            pulso.FerramentaComecou("shell");
            pulso.FerramentaTerminou("shell", falhou: true);

            papel.Tudo.Should().Contain("FALHOU");
        }

        [Fact]
        public void DepoisDaFerramenta_APROXIMA_EsperaVoltaASerEspera()
        {
            // Depois da ferramenta vem outra ida ao modelo, com outro prefill. Sem voltar a
            // fase, o pulso seguiria dizendo "escrevendo" enquanto nada escreve.
            var papel = new Papel();
            using var pulso = Novo(papel);

            pulso.Escreveu(50);
            pulso.FerramentaComecou("read");
            pulso.FerramentaTerminou("read", falhou: false);
            pulso.BaterAgora();

            papel.Tudo.Should().Contain("aguardando o primeiro token");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Fechamento
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void OResumo_SEPARA_PrefillDeGeracao()
        {
            // São dois custos diferentes com causas diferentes: o prefill depende do cache de
            // prefixo, a geração da velocidade do modelo. Somados num número só, não dá para
            // saber qual dos dois piorou.
            var papel = new Papel();
            var pulso = Novo(papel);

            pulso.Escreveu(400);
            pulso.Fim("respondeu", tokensGerados: 100);

            string ultima = papel.Linhas[^1];
            ultima.Should().Contain("respondeu")
                .And.Contain("prefill")
                .And.Contain("100 tok a")
                .And.Contain("tok/s");
        }

        [Fact]
        public void SemNenhumToken_OResumoDIZ_Isso()
        {
            // Turno que voltou vazio é diferente de turno rápido, e o resumo tem de distinguir.
            var papel = new Papel();
            var pulso = Novo(papel);

            pulso.Fim("resposta VAZIA");

            papel.Linhas[^1].Should().Contain("nenhum token recebido");
        }

        [Fact]
        public void OFim_EhIdempotente_EODisposeNaoRepete()
        {
            var papel = new Papel();
            var pulso = Novo(papel);

            pulso.Escreveu(10);
            pulso.Fim("respondeu");
            pulso.Fim("respondeu de novo");
            pulso.Dispose();

            papel.Quantas("respondeu").Should().Be(1);
            papel.Tudo.Should().NotContain("interrompido");
        }

        [Fact]
        public void TurnoQueMORRE_NoMeio_AindaFecha()
        {
            // Cancelamento e exceção passam pelo Dispose do `using`. Um pulso que só parasse no
            // caminho feliz continuaria batendo para um turno que já acabou.
            var papel = new Papel();

            using (var pulso = Novo(papel))
            {
                pulso.Escreveu(10);
            }

            papel.Linhas[^1].Should().Contain("interrompido");
        }

        [Fact]
        public void DepoisDoFim_OPulsoNaoBateMais()
        {
            var papel = new Papel();
            var pulso = Novo(papel);

            pulso.Fim("respondeu");
            int antes = papel.Linhas.Count;

            pulso.BaterAgora();

            papel.Linhas.Count.Should().Be(antes, "turno encerrado não tem pulso");
            pulso.Dispose();
        }

        // ─────────────────────────────────────────────────────────────────────
        // O relógio de verdade
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void OPulsoBATE_SOZINHO_SemNinguemChamar()
        {
            // Os outros ensaios forçam a batida para serem determinísticos. Este é o único que
            // prova o que importa de verdade: durante o prefill NINGUÉM chama nada, e a linha
            // tem de sair mesmo assim.
            var papel = new Papel();
            using var pulso = Novo(papel, TimeSpan.FromMilliseconds(40));

            var limite = Stopwatch.StartNew();
            while (papel.Quantas("aguardando o primeiro token") < 2 && limite.ElapsedMilliseconds < 3000)
                Thread.Sleep(20);

            papel.Quantas("aguardando o primeiro token").Should().BeGreaterThanOrEqualTo(2,
                "o pulso vem de um relógio próprio, não do fluxo de chunks");
        }
    }
}
