using System;
using System.Collections.Generic;
using System.Linq;
using AIB.Services;
using AIB.Services.Memory;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A conta da economia de contexto.
    /// <para>
    /// Ela era um campo somado à mão em memória — <c>_tokensCompactados</c> — que zerava ao
    /// reabrir uma conversa do histórico. A economia inteira da sessão sumia da tela sem
    /// nenhum sinal de que faltava informação. Agora o número mora no REGISTRO do capítulo e
    /// do ato, vai para o disco com eles e volta com eles.
    /// </para>
    /// <para>
    /// É a mesma lição que já apareceu no vigia silencioso e no <c>content = ""</c> fixo no
    /// código: valor escrito à mão em vez de derivado do estado.
    /// </para>
    /// </summary>
    public class EconomiaDaMemoriaTests
    {
        private static Chapter Cap(int indice, int primeiroTurno, int ultimoTurno,
                                   int crus, int proprio) =>
            new(indice, DateTime.UtcNow.ToString("o"), primeiroTurno, ultimoTurno,
                "resumo", Array.Empty<Artifact>(), crus, proprio);

        private static Act Ato(int indice, int primeiroCap, int ultimoCap,
                               int crus, int dosCapitulos, int proprio) =>
            new(indice, DateTime.UtcNow.ToString("o"), primeiroCap, ultimoCap, 0, 9,
                "resumo", Array.Empty<Artifact>(), crus, dosCapitulos, proprio);

        // ─────────────────────────────────────────────────────────────────────
        // O registro sabe quanto custou
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void OCapitulo_SABE_OQueEngoliuEOQuePesa()
        {
            var capitulo = Cap(0, 0, 4, crus: 9_933, proprio: 143);

            capitulo.TokensDosTurnos.Should().Be(9_933);
            capitulo.TokensDoCapitulo.Should().Be(143);
            capitulo.Economia.Should().Be(9_790);
            capitulo.TemMedida.Should().BeTrue();
        }

        [Fact]
        public void ResumoMaiorQueOMaterial_NaoEconomiza_MenosQueZero()
        {
            // Um turno de duas linhas resumido em três produz economia NEGATIVA na conta crua.
            // Mostrar "-30" acusaria o sistema de piorar o prompt; ele apenas não melhorou.
            Cap(0, 0, 0, crus: 40, proprio: 90).Economia.Should().Be(0);
        }

        [Fact]
        public void CapituloAntigo_SemMedida_NaoMente_DizendoZero()
        {
            // Gravado antes de a medição existir: os campos vêm 0 do JSON. Zero como ECONOMIA
            // afirmaria que ele não poupou nada; a verdade é que ninguém mediu, e quem lê
            // precisa saber a diferença.
            var antigo = new Chapter(0, "x", 0, 3, "resumo", Array.Empty<Artifact>());

            antigo.TemMedida.Should().BeFalse();
            antigo.Economia.Should().Be(0);
        }

        [Fact]
        public void OAto_MEDE_ContraOCru_ESepara_OGanhoDaPromocao()
        {
            // Duas economias diferentes, e confundi-las faria o ato levar o crédito do trabalho
            // que os capítulos já tinham feito.
            var ato = Ato(0, 0, 3, crus: 40_000, dosCapitulos: 600, proprio: 210);

            ato.Economia.Should().Be(39_790, "contra o CRU, que é o que a conversa custaria");
            ato.EconomiaDaPromocao.Should().Be(390, "e a promoção em si rendeu só isto");
        }

        // ─────────────────────────────────────────────────────────────────────
        // A soma
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void OCru_SOMA_OsCapitulos_ENaoOsAtos()
        {
            // Cada turno passou por exatamente um capítulo. Somar os atos junto contaria o
            // mesmo turno duas vezes e dobraria a economia na tela.
            var camada = new MemoryLayer();
            camada.AddRange(new[] { Cap(0, 0, 2, 5_000, 100), Cap(1, 3, 5, 4_000, 90) });
            camada.Add(Ato(0, 0, 1, crus: 9_000, dosCapitulos: 190, proprio: 120));

            camada.TokensCrus.Should().Be(9_000);
        }

        [Fact]
        public void AMemoria_PESA_OAto_ENaoOsCapitulosQueEleAbsorveu()
        {
            // Capítulo coberto por ato não vai ao prompt — RenderChapters filtra por
            // LastCoveredChapter. Contá-lo aqui inventaria um peso que ninguém paga.
            var camada = new MemoryLayer();
            camada.AddRange(new[] { Cap(0, 0, 2, 5_000, 100), Cap(1, 3, 5, 4_000, 90) });
            camada.Add(Ato(0, 0, 1, crus: 9_000, dosCapitulos: 190, proprio: 120));

            camada.TokensDaMemoria.Should().Be(120);
            camada.Economia.Should().Be(8_880);
        }

        [Fact]
        public void CapituloSOLTO_EntraNoPeso()
        {
            var camada = new MemoryLayer();
            camada.AddRange(new[] { Cap(0, 0, 2, 5_000, 100), Cap(1, 3, 5, 4_000, 90) });
            camada.Add(Ato(0, 0, 0, crus: 5_000, dosCapitulos: 100, proprio: 70));

            // O ato cobre só o capítulo 0. O 1 continua solto e continua no prompt.
            camada.TokensDaMemoria.Should().Be(70 + 90);
        }

        [Fact]
        public void UmCapituloSemMedida_TORNA_AContaIncompleta()
        {
            // Somar zero com medidas reais produz uma economia MENOR que a verdadeira, e sem
            // este sinal isso aconteceria em silêncio.
            var camada = new MemoryLayer();
            camada.AddRange(new[]
            {
                new Chapter(0, "x", 0, 2, "resumo", Array.Empty<Artifact>()),
                Cap(1, 3, 5, 4_000, 90)
            });

            camada.MedidaCompleta.Should().BeFalse();
        }

        [Fact]
        public void SemCapituloNenhum_AContaNaoEhCompleta_EhInexistente()
        {
            // Conversa que ainda não compactou nada não tem medida "completa": não tem medida.
            // A interface usa isto para não anunciar 0% de economia no primeiro turno.
            new MemoryLayer().MedidaCompleta.Should().BeFalse();
            new MemoryLayer().Economia.Should().Be(0);
        }

        // ─────────────────────────────────────────────────────────────────────
        // O relatório que a tela lê
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ORelatorio_LEVA_AsParcelas_ENaoSoOTotal()
        {
            // Dois números e uma cor respondem "está economizando?". Não respondem "de onde
            // vem esse número?", que é a pergunta do dia em que a conta parece errada.
            var r = new TokenReport(12_408, 5_102, 16_384,
                Cru: 9_000, Memoria: 120, MemoriaDosRegistros: 93, Capitulos: 2, Atos: 1);

            r.Economia.Should().Be(8_880, "o que a COMPACTAÇÃO fez: o cru menos a memória");
            r.EconomiaPct.Should().Be(72);
            r.Cru.Should().Be(9_000);
            r.Memoria.Should().Be(120);
        }

        [Fact]
        public void AEconomia_NaoEh_ADiferencaDosDoisNumerosDaBarra()
        {
            // Total - Contexto inclui o que a REABERTURA descartou. Creditar isso à compactação
            // a faria parecer melhor por trabalho que não fez — a mesma regra que já vale para
            // a poda de emergência.
            var r = new TokenReport(4_292, 1_838, 9_216,
                Cru: 200, Memoria: 128, MemoriaDosRegistros: 101, Descartado: 2_382, Capitulos: 1);

            r.Economia.Should().Be(72, "a compactação resumiu 200 tokens em 128");
            r.ForaDoContexto.Should().Be(2_454);
            (r.Economia + r.Descartado).Should().Be(r.ForaDoContexto,
                "os dois números da barra têm de fechar com as duas causas");
        }

        [Fact]
        public void ReabertaSemCapituloNENHUM_AindaMostra_OCustoCru()
        {
            // Nada compactado e mesmo assim o total é maior que o contexto: a reabertura sozinha
            // já tira as ferramentas do prompt. Esconder isso é o que fazia uma conversa de
            // 9.144 tokens reaparecer como 1.838 sem explicação.
            var r = new TokenReport(3_000, 900, 9_216, Descartado: 2_100);

            r.Economia.Should().Be(0, "não houve compactação");
            r.EconomiaPct.Should().BeNull();
            r.ForaDoContexto.Should().Be(2_100);
            r.Total.Should().BeGreaterThan(r.Contexto, "a barra precisa continuar mostrando os dois");
        }

        [Fact]
        public void AsParcelas_FECHAM_ComOCabecalhoDaFaixa()
        {
            // Medido na sessão real: o capítulo custa 101 e a faixa no prompt pesa 128. A
            // diferença é o cabeçalho do bloco — duas linhas pagas UMA vez, existindo um
            // capítulo ou vinte. Sem linha própria ela virava um buraco de 27 tokens no meio de
            // uma conta que o usuário estava conferindo.
            var r = new TokenReport(1_910, 1_838, 9_216,
                Cru: 200, Memoria: 128, MemoriaDosRegistros: 101, Capitulos: 1);

            r.DiferencaDaFaixa.Should().Be(27);
            (r.Cru - r.Memoria).Should().Be(r.Economia, "as três linhas da tela têm de fechar");
        }

        [Fact]
        public void OQueAReaberturaDescarta_TEM_LinhaPropria()
        {
            // Medido na sessão real de 10/09: 9.144 tokens de prompt ao vivo, 1.838 ao reabrir.
            // A diferença são 2.382 tokens de chamada e resultado de ferramenta que a reabertura
            // não traz de volta — só as falas voltam, porque um tool_calls sem o resultado
            // correspondente quebra a requisição seguinte.
            //
            // Sem esta linha a conta reaberta contradiz a memória de quem esteve na conversa, e
            // quem confere conclui que o contador está errado.
            var r = new TokenReport(1_910, 1_838, 9_216,
                Cru: 200, Memoria: 128, MemoriaDosRegistros: 101, Descartado: 2_382, Capitulos: 1);

            r.Descartado.Should().Be(2_382);

            // E NÃO entra no total: quem descartou foi a reabertura, não a compactação. Mesma
            // regra da poda de emergência — creditar aqui faria o sistema de capítulos parecer
            // melhor por trabalho que ele não fez.
            r.Economia.Should().Be(72);
        }

        [Fact]
        public void SemReabertura_NaoHaDescarte_ENemLinha()
        {
            // Conversa que nunca foi reaberta não perdeu ferramenta nenhuma. A linha some em vez
            // de anunciar um zero que faria pensar em perda.
            new TokenReport(1_000, 900, 9_216).Descartado.Should().Be(0);
        }

        [Fact]
        public void CapituloAparado_PelaCota_TemOutroNome()
        {
            // Diferença negativa não é cabeçalho: é capítulo que a cota deixou de fora do
            // prompt. Chamar os dois pelo mesmo nome esconderia memória que não está sendo
            // enviada.
            var r = new TokenReport(9_000, 8_000, 9_216,
                Cru: 4_000, Memoria: 300, MemoriaDosRegistros: 900, Capitulos: 6);

            r.DiferencaDaFaixa.Should().Be(-600);
        }

        [Fact]
        public void SemCompactacao_NaoHaPorcentagem_NemEconomia()
        {
            // null é diferente de zero: zero afirmaria que o sistema rodou e não economizou
            // nada, quando a verdade é que ele ainda não teve o que fazer.
            var r = new TokenReport(3_000, 3_000, 16_384);

            r.EconomiaPct.Should().BeNull();
            r.Economia.Should().Be(0);
        }

        [Fact]
        public void ORelatorio_NASCE_CompletoPorPadrao()
        {
            // Quem constrói um relatório sem falar de medida está numa conversa sem capítulos
            // antigos. O padrão não pode ser "incompleto", senão toda conversa nova exibiria o
            // aviso de piso.
            new TokenReport(1, 1, 2).MedidaCompleta.Should().BeTrue();
        }
    }
}
