using AIB.Services.Memory;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Repartição do orçamento entre memória e conversa. Pura, sem IO e sem modelo.
    /// </summary>
    public class MemoryBudgetTests
    {
        [Fact]
        public void AsTresFaixasSomamExatamenteAFatiaDeMemoria()
        {
            var quota = MemoryBudget.Compute(levelBudget: 12288, fixedPrefixTokens: 1000);

            // Capítulos recebem o RESTO, não a fração arredondada: nenhum token some.
            quota.Memory.Should().Be((int)((12288 - 1000) * MemoryBudget.MemoryFraction));
            quota.Facts.Should().BeGreaterThan(0);
            quota.Acts.Should().BeGreaterThan(quota.Facts);
            quota.Chapters.Should().BeGreaterThan(quota.Acts);
        }

        [Fact]
        public void MemoriaMaisVivo_NuncaPassaDoQueSobrouDoPrefixo()
        {
            var quota = MemoryBudget.Compute(8192, 3900);

            (quota.Memory + quota.Live).Should().Be(8192 - 3900);
        }

        [Fact]
        public void AlmaGrandeEmNivelBaixo_DesligaAMemoriaEmVezDeSufocarAConversa()
        {
            // Alma de ~3.900 tokens (a Ayano) contra o teto do nível 1.
            int sobra = 8192 - 6500;
            sobra.Should().BeLessThan(MemoryBudget.MinimumAvailable);

            var quota = MemoryBudget.Compute(8192, 6500);

            quota.IsOff.Should().BeTrue("agente sem memória funciona; sem espaço para conversar, não");
            quota.Live.Should().Be(sobra, "o pouco que sobrou vai inteiro para a conversa");
        }

        [Fact]
        public void PrefixoMaiorQueOOrcamento_ZeraTudoSemLancar()
        {
            var quota = MemoryBudget.Compute(8192, 9000);

            quota.IsOff.Should().BeTrue();
            quota.Live.Should().Be(0);
        }

        [Fact]
        public void Gatilho_DisparaAntesDoEstouro()
        {
            var quota = MemoryBudget.Compute(12288, 1000);

            int gatilho = MemoryBudget.CompactionThreshold(quota);

            // Se o gatilho fosse 100%, a poda de emergência entraria primeiro e comeria
            // justamente as mensagens que o capítulo iria resumir.
            gatilho.Should().BeLessThan(quota.Live);
            gatilho.Should().Be((int)(quota.Live * MemoryBudget.CompactionTrigger));
        }

        [Fact]
        public void OrcamentoMaior_DaMaisEspacoParaAsDuasPartes()
        {
            var baixo = MemoryBudget.Compute(8192, 1000);
            var alto = MemoryBudget.Compute(12288, 1000);

            alto.Memory.Should().BeGreaterThan(baixo.Memory);
            alto.Live.Should().BeGreaterThan(baixo.Live);
        }
    }
}
