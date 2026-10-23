using System;
using System.IO;
using System.Linq;
using AIB.Services;
using AIB.Services.Memory;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O facts.md é do usuário e o facts.index.jsonl é da máquina. Quase todo teste aqui existe
    /// para provar que a segunda nunca desfaz o que a primeira decidiu.
    /// </summary>
    public class FactStoreTests : IDisposable
    {
        private readonly string _dir;
        private readonly FactStore _store;

        public FactStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "AIB_Fatos_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _store = new FactStore(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static FactCandidate Fato(string chave, string linha) => new(chave, linha);

        // ─────────────────────────────────────────────────────────────────────
        // Escrita
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Promote_CriaOArquivoComCabecalhoExplicativo()
        {
            _store.Promote(new[] { Fato("arquivo|C:\\a.cs", "- arquivo relevante: C:\\a.cs") });

            string texto = Claro.Texto(_store.FactsPath);

            texto.Should().Contain("# Fatos duráveis");
            texto.Should().Contain("- arquivo relevante: C:\\a.cs");
            // O usuário precisa saber que pode editar, senão não edita.
            texto.Should().Contain("apagar");
        }

        [Fact]
        public void Promote_DevolveQuantosEntraram()
        {
            int entraram = _store.Promote(new[]
            {
                Fato("a", "- um"),
                Fato("b", "- dois")
            });

            entraram.Should().Be(2);
        }

        [Fact]
        public void Promote_NaoGravaOMesmoFatoDuasVezes()
        {
            _store.Promote(new[] { Fato("a", "- um") });
            int segunda = _store.Promote(new[] { Fato("a", "- um") });

            segunda.Should().Be(0);
            _store.ReadFacts().Should().ContainSingle();
        }

        [Fact]
        public void FatoApagadoPeloUsuario_NaoVoltaSozinho()
        {
            // O motivo de existir o registro separado. Sem ele, um fato errado que o usuário
            // apagasse ressuscitaria na promoção seguinte — a memória discutindo com o dono.
            _store.Promote(new[] { Fato("a", "- fato errado") });

            var restantes = Claro.Linhas(_store.FactsPath)
                .Where(l => !l.Contains("fato errado"));
            File.WriteAllLines(_store.FactsPath, restantes);

            _store.Promote(new[] { Fato("a", "- fato errado") });

            _store.ReadFacts().Should().BeEmpty();
        }

        [Fact]
        public void Promote_NaoReescreveOQueOUsuarioEditou()
        {
            _store.Promote(new[] { Fato("a", "- original") });

            string editado = Claro.Texto(_store.FactsPath).Replace("- original", "- corrigido à mão");
            File.WriteAllText(_store.FactsPath, editado);

            _store.Promote(new[] { Fato("b", "- novo") });

            var fatos = _store.ReadFacts();
            fatos.Should().Contain("- corrigido à mão");
            fatos.Should().Contain("- novo");
            fatos.Should().NotContain("- original");
        }

        [Fact]
        public void Promote_ToleraNulo()
        {
            _store.Promote(null).Should().Be(0);
        }

        [Fact]
        public void Promote_IgnoraCandidatoSemChave()
        {
            _store.Promote(new[] { Fato("", "- sem chave") }).Should().Be(0);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Leitura
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ReadFacts_SemArquivo_DevolveVazio()
        {
            _store.ReadFacts().Should().BeEmpty();
        }

        [Fact]
        public void ReadFacts_IgnoraCabecalhosComentariosEBranco()
        {
            File.WriteAllText(_store.FactsPath,
                "# Fatos duráveis\n\n<!-- comentário -->\ntexto solto\n- um fato\n\n- outro\n");

            _store.ReadFacts().Should().Equal("- um fato", "- outro");
        }

        [Fact]
        public void ReadFacts_PreservaAOrdemDoArquivo()
        {
            // A ordem é escolha do usuário: é ela que decide o que sobrevive ao corte da cota.
            File.WriteAllText(_store.FactsPath, "- terceiro\n- primeiro\n- segundo\n");

            _store.ReadFacts().Should().Equal("- terceiro", "- primeiro", "- segundo");
        }

        [Fact]
        public void ReadFacts_NaoConfundeTracoSozinhoComFato()
        {
            File.WriteAllText(_store.FactsPath, "-\n- \n- de verdade\n");

            _store.ReadFacts().Should().Equal("- de verdade");
        }

        [Fact]
        public void RegistroCorrompido_NaoDerrubaALeitura()
        {
            _store.Promote(new[] { Fato("a", "- um") });
            File.AppendAllText(_store.LedgerPath, "{isto não é json\n");

            _store.ReadPromotedKeys().Should().Contain("a");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Renderização
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Render_CortaNoFimQuandoNaoCabeTudo()
        {
            var counter = new TokenCounter();
            var fatos = Enumerable.Range(0, 50).Select(i => $"- fato numero {i} com algum texto").ToList();
            var cota = new MemoryQuota(Facts: 40, Acts: 0, Chapters: 0, Live: 1000);

            string bloco = FactStore.Render(fatos, cota, counter);

            counter.CountText(bloco).Should().BeLessThanOrEqualTo(40);
            bloco.Should().Contain("fato numero 0", "o topo do arquivo é o que o usuário priorizou");
            bloco.Should().NotContain("fato numero 49");
        }

        [Fact]
        public void Render_CotaZerada_DevolveVazio()
        {
            var cota = new MemoryQuota(Facts: 0, Acts: 0, Chapters: 0, Live: 1000);

            FactStore.Render(new[] { "- um" }, cota, new TokenCounter()).Should().BeEmpty();
        }

        [Fact]
        public void Render_CotaMinusculaQueSoCabeOTitulo_DevolveVazio()
        {
            // Título sozinho é ruído: anuncia uma seção que não tem conteúdo.
            var cota = new MemoryQuota(Facts: 5, Acts: 0, Chapters: 0, Live: 1000);

            FactStore.Render(new[] { "- um fato bem comprido que nunca caberia nessa cota" }, cota, new TokenCounter())
                .Should().BeEmpty();
        }

        [Fact]
        public void Render_SemFatos_DevolveVazio()
        {
            var cota = new MemoryQuota(Facts: 500, Acts: 0, Chapters: 0, Live: 1000);

            FactStore.Render(Array.Empty<string>(), cota, new TokenCounter()).Should().BeEmpty();
            FactStore.Render(null, cota, new TokenCounter()).Should().BeEmpty();
        }
    }
}
