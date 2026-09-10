using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using AIB.Services;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O contador de tokens do rodapé — §3.8.
    /// <para>
    /// O que ele mede mudou: era a fatia do prompt que o cache do Ollama não precisou
    /// reprocessar — uma medida do servidor, que ia a zero quando outra completação despejava a
    /// fatia de KV e não dizia nada sobre a AIB. Agora mede o sistema de capítulos e atos:
    /// quanto a conversa pesaria inteira contra quanto ela pesa depois de resumida.
    /// </para>
    /// </summary>
    public class TokenCounterUiTests
    {
        private const BindingFlags Privados = BindingFlags.NonPublic | BindingFlags.Instance;

        private static void Atualizar(ChatWindow janela, TokenReport relatorio) =>
            typeof(ChatWindow).GetMethod("UpdateTokenCounterUI", Privados)!
                .Invoke(janela, new object?[] { relatorio });

        /// <summary>
        /// O texto da barra, montado a partir dos Runs.
        /// <para>
        /// O total vai TACHADO, e riscar metade de uma linha exige inlines separados. A
        /// propriedade Text de um TextBlock não acompanha alterações feitas direto na coleção
        /// Inlines — ela continua devolvendo o que o XAML escreveu.
        /// </para>
        /// </summary>
        private static string Texto(ChatWindow janela)
        {
            var caixa = (TextBlock)janela.FindName("TokenCounterText");

            return caixa.Inlines.Count > 0
                ? string.Concat(caixa.Inlines.OfType<Run>().Select(r => r.Text))
                : caixa.Text;
        }

        private static Run? PrimeiroRun(ChatWindow janela) =>
            ((TextBlock)janela.FindName("TokenCounterText")).Inlines.OfType<Run>().FirstOrDefault();

        private static Brush Cor(ChatWindow janela) =>
            ((TextBlock)janela.FindName("TokenCounterText")).Foreground;

        [Fact]
        public void ComEconomia_MostraOsDoisNumerosEOTeto()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Atualizar(janela, new TokenReport(Total: 12000, Contexto: 3000, Max: 8704));

                Texto(janela).Should().Contain("12.000", "o que a conversa pesaria, tachado");
                Texto(janela).Should().Contain("3.000", "e o que ela pesa");
                Texto(janela).Should().Contain("|", "depois da barra vem o teto do nivel");
                Texto(janela).Should().Contain("8.704");
                Texto(janela).Should().NotContain("%", "a porcentagem saiu: os dois numeros ja dizem o quanto foi poupado");

                janela.Close();
            });
        }

        [Fact]
        public void SemCompactacao_NaoRepeteONumeroNemInventaPorcentagem()
        {
            // Antes do primeiro capítulo os dois números são o mesmo. Escrever "1.204 > 1.204
            // (-0%)" ocuparia o rodapé para não dizer nada, e o "-0%" ainda acusaria o sistema
            // de não estar economizando quando ele só não teve o que fazer.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Atualizar(janela, new TokenReport(Total: 1204, Contexto: 1204, Max: 8704));

                Texto(janela).Should().NotContain("%");
                Texto(janela).Should().Contain("1.204 tokens");
                Texto(janela).Should().Contain("| 8.704", "o teto aparece com ou sem compactacao");

                janela.Close();
            });
        }

        [Fact]
        public void EconomiaBAIXA_NaoPinta_DeAlarme()
        {
            // A cor media a ECONOMIA, e a escala punia conversa curta: um capítulo que resumiu
            // 200 tokens em 128 fez o trabalho dele e a barra saía magenta, acusando o sistema
            // de falhar. Mesma armadilha do "-0%" no primeiro turno, um nível acima.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Atualizar(janela, new TokenReport(
                    Total: 4292, Contexto: 1838, Max: 9216, Cru: 200, Memoria: 128,
                    Descartado: 2382, Capitulos: 1));

                Cor(janela).Should().BeSameAs(janela.FindResource("TextSecondaryBrush"),
                    "20% de ocupação não é motivo de alarme, por menor que seja a economia");

                janela.Close();
            });
        }

        [Fact]
        public void ContextoQuaseCHEIO_AvisaEmVermelho()
        {
            // É esta a grandeza acionável: passar de 90% quer dizer que a poda de emergência
            // está perto, e a poda descarta SEM substituto.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Atualizar(janela, new TokenReport(Total: 9000, Contexto: 8500, Max: 9216));

                Cor(janela).Should().BeSameAs(janela.FindResource("DangerTextBrush"));

                janela.Close();
            });
        }

        [Fact]
        public void ContextoApertando_AvisaEmLaranja()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Atualizar(janela, new TokenReport(Total: 8000, Contexto: 7000, Max: 9216));

                Cor(janela).Should().BeSameAs(janela.FindResource("WarnBrush"));

                janela.Close();
            });
        }

        [Fact]
        public void OTotal_VaiTACHADO_ESoEle()
        {
            // Riscar diz "este é o preço que a conversa NÃO está pagando" sem precisar de
            // legenda, e deixa o número vivo ser o que salta aos olhos.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Atualizar(janela, new TokenReport(
                    Total: 4292, Contexto: 1838, Max: 9216, Cru: 200, Memoria: 128,
                    Descartado: 2382, Capitulos: 1));

                var primeiro = PrimeiroRun(janela);
                primeiro.Should().NotBeNull();
                primeiro!.Text.Should().Be("4.292");
                primeiro.TextDecorations.Should().BeSameAs(System.Windows.TextDecorations.Strikethrough);

                var caixa = (TextBlock)janela.FindName("TokenCounterText");
                caixa.Inlines.OfType<Run>().Skip(1)
                     .Should().OnlyContain(r => r.TextDecorations == null || r.TextDecorations.Count == 0,
                         "só o total é riscado; o número vivo não");

                janela.Close();
            });
        }

        [Fact]
        public void SemTotalASeparar_NaoHaRunTachado()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Atualizar(janela, new TokenReport(Total: 1204, Contexto: 1204, Max: 8704));

                var caixa = (TextBlock)janela.FindName("TokenCounterText");
                caixa.Inlines.OfType<Run>()
                     .Should().OnlyContain(r => r.TextDecorations == null || r.TextDecorations.Count == 0);

                janela.Close();
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // A conta, sem interface
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A porcentagem é a fatia do CUSTO CRU que a compactação tirou — e não a diferença
        /// entre os dois números da barra.
        /// <para>
        /// Os dois passaram a divergir quando o total ganhou o que a reabertura descarta: numa
        /// conversa reaberta, <c>Total - Contexto</c> é quase todo tráfego de ferramenta que a
        /// compactação nunca tocou, e usá-lo aqui daria a ela crédito por trabalho alheio.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(1000, 800, 50, 75)]
        [InlineData(1000, 150, 50, 10)]
        [InlineData(3, 2, 0, 67)]
        public void EconomiaPct_EhAFatiaDoCruQueACompactacaoTirou(
            int total, int cru, int memoria, int esperado) =>
            new TokenReport(total, 1, 8704, Cru: cru, Memoria: memoria)
                .EconomiaPct.Should().Be(esperado);

        [Theory]
        [InlineData(0, 0)]
        [InlineData(500, 500)]
        [InlineData(300, 900)]
        public void SemGanho_NaoHaPorcentagem(int cru, int memoria) =>
            new TokenReport(1000, 900, 8704, Cru: cru, Memoria: memoria)
                .EconomiaPct.Should().BeNull(
                    "null diz 'ainda não houve compactação'; zero afirmaria que ela rodou e falhou");

        [Fact]
        public void ReabertaSemCompactacao_MostraOsDoisNumeros_MesmoSemPorcentagem()
        {
            // A barra passou a decidir pelo TOTAL, e não pela economia. Sem isto uma conversa
            // reaberta sem capítulo nenhum exibia só o contexto — 1.838 onde a pessoa se
            // lembrava de 9.144, sem nada na tela que explicasse a diferença.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var janela = JanelaDeEnsaio.Nova();

                Atualizar(janela, new TokenReport(
                    Total: 3000, Contexto: 900, Max: 8704, Descartado: 2100));

                Texto(janela).Should().Contain("3.000");
                Texto(janela).Should().Contain("900 tokens");
                Texto(janela).Should().NotContain("%");

                janela.Close();
            });
        }
    }
}
