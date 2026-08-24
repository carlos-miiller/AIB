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
    /// A montagem do bloco de memória. A ordem interna — fatos, atos, capítulos — não é
    /// estética: ela segue a frequência de mudança, e é o que faz o cache de prefixo sobreviver
    /// a um capítulo novo.
    /// </summary>
    public class MemoryLayerTests
    {
        private static readonly TokenCounter Counter = new();

        private static MemoryQuota Larga => new(Facts: 4000, Acts: 4000, Chapters: 4000, Live: 4000);

        private static Chapter Capitulo(int indice, string resumo, params Artifact[] artefatos) =>
            new(indice, "2026-08-24T00:00:00Z", indice * 2, indice * 2 + 1, resumo, artefatos);

        private static Act Ato(int indice, int primeiro, int ultimo, string resumo) =>
            new(indice, "2026-08-24T00:00:00Z", primeiro, ultimo, primeiro * 2, ultimo * 2 + 1,
                resumo, Array.Empty<Artifact>());

        // ─────────────────────────────────────────────────────────────────────
        // Ordem e composição
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Render_PoeFatosAntesDeAtosEAtosAntesDeCapitulos()
        {
            var camada = new MemoryLayer();
            camada.SetFacts(new[] { "- um fato durável" });
            camada.Add(Ato(0, 0, 3, "o arco do primeiro ato"));
            camada.Add(Capitulo(4, "capítulo solto"));

            string bloco = camada.Render(Larga, Counter);

            int fatos = bloco.IndexOf("um fato durável", StringComparison.Ordinal);
            int atos = bloco.IndexOf("o arco do primeiro ato", StringComparison.Ordinal);
            int capitulos = bloco.IndexOf("capítulo solto", StringComparison.Ordinal);

            fatos.Should().BeGreaterThan(-1);
            fatos.Should().BeLessThan(atos);
            atos.Should().BeLessThan(capitulos);
        }

        [Fact]
        public void Render_SemNada_DevolveVazio()
        {
            new MemoryLayer().Render(Larga, Counter).Should().BeEmpty();
        }

        [Fact]
        public void Render_SoComFatos_NaoAnunciaMemoriaDaConversa()
        {
            // Sessão nova com facts.md antigo: não houve conversa nenhuma para resumir ainda.
            var camada = new MemoryLayer();
            camada.SetFacts(new[] { "- Carlo trabalha no diretório C:\\Users\\Carlo\\CPAPS\\AIB" });

            string bloco = camada.Render(Larga, Counter);

            bloco.Should().Contain("Fatos duráveis");
            bloco.Should().NotContain("Memória da conversa");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Ato substitui capítulo
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void CapituloCobertoPorAto_NaoEhRenderizadoDeNovo()
        {
            // Renderizar os dois contaria a mesma coisa duas vezes, gastando cota para repetir.
            var camada = new MemoryLayer();
            for (int i = 0; i < 4; i++) camada.Add(Capitulo(i, $"resumo do capitulo {i}"));
            camada.Add(Ato(0, 0, 3, "o arco inteiro"));

            string bloco = camada.Render(Larga, Counter);

            bloco.Should().Contain("o arco inteiro");
            bloco.Should().NotContain("resumo do capitulo 0");
            bloco.Should().NotContain("resumo do capitulo 3");
        }

        [Fact]
        public void CapituloNascidoDepoisDoAto_ContinuaAparecendo()
        {
            var camada = new MemoryLayer();
            for (int i = 0; i < 4; i++) camada.Add(Capitulo(i, $"resumo do capitulo {i}"));
            camada.Add(Ato(0, 0, 3, "o arco inteiro"));
            camada.Add(Capitulo(4, "resumo do capitulo 4"));

            camada.Render(Larga, Counter).Should().Contain("resumo do capitulo 4");
        }

        [Fact]
        public void UncoveredChapters_SoOsQueAindaNaoViraramAto()
        {
            var camada = new MemoryLayer();
            for (int i = 0; i < 6; i++) camada.Add(Capitulo(i, $"c{i}"));
            camada.Add(Ato(0, 0, 3, "arco"));

            camada.UncoveredChapters.Select(c => c.Index).Should().Equal(4, 5);
            camada.LastCoveredChapter.Should().Be(3);
        }

        [Fact]
        public void NextActIndex_SegueOUltimoAto()
        {
            var camada = new MemoryLayer();
            camada.NextActIndex.Should().Be(0);

            camada.Add(Ato(0, 0, 3, "arco"));
            camada.NextActIndex.Should().Be(1);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Cotas
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void CotaDeAtoNaoRoubaNemCedeEspacoAoCapitulo()
        {
            // Cotas independentes: sobra de ato virando espaço de capítulo faria o tamanho do
            // bloco de capítulos mudar toda vez que um ato nascesse, e mexer no meio do prefixo
            // custa o mesmo que mexer no começo.
            var camada = new MemoryLayer();
            camada.Add(Capitulo(0, string.Concat(Enumerable.Repeat("texto de capitulo. ", 60))));

            var comAtoFolgado = new MemoryQuota(0, 4000, 40, 1000);
            var semAtoNenhum = new MemoryQuota(0, 0, 40, 1000);

            camada.Render(comAtoFolgado, Counter).Should().Be(camada.Render(semAtoNenhum, Counter));
        }

        [Fact]
        public void QuandoNaoCabeTudo_OQueSePerdeEhOPassadoDistante()
        {
            var camada = new MemoryLayer();
            camada.Add(Capitulo(0, string.Concat(Enumerable.Repeat("antigo. ", 80))));
            camada.Add(Capitulo(1, "recente e curto"));

            string bloco = camada.Render(new MemoryQuota(0, 0, 30, 1000), Counter);

            bloco.Should().Contain("recente e curto");
            bloco.Should().NotContain("antigo.");
        }

        [Fact]
        public void AtosSaemEmOrdemCronologica()
        {
            // Selecionar do mais recente é para caber; renderizar de trás para frente faria o
            // modelo ler a conversa ao contrário.
            var camada = new MemoryLayer();
            camada.Add(Ato(0, 0, 3, "primeiro arco"));
            camada.Add(Ato(1, 4, 7, "segundo arco"));

            string bloco = camada.Render(Larga, Counter);

            bloco.IndexOf("primeiro arco", StringComparison.Ordinal)
                .Should().BeLessThan(bloco.IndexOf("segundo arco", StringComparison.Ordinal));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Ciclo de vida
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Clear_ApagaCapitulosEAtosMasNaoOsFatos()
        {
            // "Durável" que não sobrevive a um reset não é durável.
            var camada = new MemoryLayer();
            camada.SetFacts(new[] { "- fato que atravessa sessões" });
            camada.Add(Capitulo(0, "capitulo"));
            camada.Add(Ato(0, 0, 0, "ato"));

            camada.Clear();

            camada.Chapters.Should().BeEmpty();
            camada.Acts.Should().BeEmpty();
            camada.Facts.Should().ContainSingle();
            camada.Render(Larga, Counter).Should().Contain("fato que atravessa sessões");
        }

        [Fact]
        public void SetFacts_ToleraNulo()
        {
            var camada = new MemoryLayer();
            camada.SetFacts(new[] { "- um" });
            camada.SetFacts(null);

            camada.Facts.Should().BeEmpty();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Renderização do ato
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void AtoMostraAFaixaDeCapitulosQueCobre()
        {
            var ato = Ato(0, 0, 3, "arco");

            ato.Render().Should().StartWith("### Ato 1 (capítulos 1–4)");
        }

        [Fact]
        public void AtoDeUmCapituloSoNaoImprimeFaixa()
        {
            Ato(2, 5, 5, "arco curto").Render().Should().StartWith("### Ato 3 (capítulo 6)");
        }

        [Fact]
        public void AtoImprimeArtefatosLiterais()
        {
            var ato = new Act(0, "2026-08-24T00:00:00Z", 0, 3, 0, 7, "arco",
                new List<Artifact> { new(ArtifactKind.FileWritten, "write_file", @"C:\x\a.cs", false) });

            ato.Render().Should().Contain(@"C:\x\a.cs");
        }
    }
}
