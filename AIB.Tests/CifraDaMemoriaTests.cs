using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Memory;
using AIB.Services.Tools;
using FluentAssertions;
using OpenAI.Chat;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Conversas, fatos e lembretes cifrados em disco. Pedido: "bloquear também o raw e facts,
    /// visto que podem ter coisas sensíveis sobre o usuário" — com exportação em texto claro,
    /// porque a cifra é da conta do Windows e não sobrevive a uma reinstalação.
    /// </summary>
    public class CifraDaMemoriaTests : IDisposable
    {
        private const string Segredo = "o servidor do financeiro fica em 10.0.0.7";

        private readonly string _raiz = Path.Combine(Path.GetTempPath(), "aib-cifra-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_raiz, true); } catch { }
        }

        private string Memoria => Path.Combine(_raiz, "memory");

        private static Turn Turno(int indice, string texto) => new(indice, new List<ChatMessage>
        {
            ChatMessage.CreateUserMessage(texto),
            ChatMessage.CreateAssistantMessage("anotado")
        });

        private static string Bruto(string caminho) => File.ReadAllText(caminho);

        // ── A cifra ────────────────────────────────────────────────────

        [Fact]
        public void LinhaCifrada_NaoMostraOTexto_EAbreIgual()
        {
            string linha = ArquivoCifrado.Cifrar(Segredo);

            linha.Should().StartWith(ArquivoCifrado.Marca).And.NotContain("financeiro");
            ArquivoCifrado.Abrir(linha, out string claro).Should().BeTrue();
            claro.Should().Be(Segredo);

            // Texto claro passa como veio: é a linha de antes da cifra.
            ArquivoCifrado.Abrir("{\"Index\":0}").Should().Be("{\"Index\":0}");
        }

        [Fact]
        public void LinhaQueNaoAbre_EhPulada_ENaoDerrubaALeitura()
        {
            // Gravada por outra conta do Windows, ou cortada por uma queda no meio da escrita.
            string arquivo = Path.Combine(_raiz, "a.jsonl");
            ArquivoCifrado.Acrescentar(arquivo, "primeira");
            File.AppendAllText(arquivo, ArquivoCifrado.Marca + "isto-nao-e-base64" + Environment.NewLine);
            ArquivoCifrado.Acrescentar(arquivo, "terceira");

            ArquivoCifrado.Linhas(arquivo).Should().Equal("primeira", "terceira");
        }

        [Fact]
        public void Acrescentar_NaoColaNaUltimaLinha_QuandoOArquivoNaoTerminaEmQuebra()
        {
            // Visto no ensaio dos fatos: o arquivo regravado sem quebra no fim recebia a linha
            // nova colada na última, e as duas ficavam ilegíveis.
            string arquivo = Path.Combine(_raiz, "b.md");
            Directory.CreateDirectory(_raiz);
            File.WriteAllText(arquivo, "- editado à mão");

            ArquivoCifrado.Acrescentar(arquivo, "- novo");

            ArquivoCifrado.Linhas(arquivo).Should().Equal("- editado à mão", "- novo");
        }

        // ── O que vai para o disco ─────────────────────────────────────

        [Fact]
        public void ORawOsCapitulosEOTurnoAberto_SaemCifrados_EVoltamInteiros()
        {
            var memoria = new SessionMemory("s1", Memoria);
            memoria.AppendTurn(Turno(0, Segredo)).Should().BeTrue();
            memoria.GravarTurnoAberto(Turno(1, Segredo + " de novo"), "id-1");

            Bruto(memoria.RawPath).Should().NotContain("financeiro");
            Bruto(memoria.TurnoAbertoPath).Should().NotContain("financeiro");

            memoria.ReadTurns().Should().ContainSingle().Which.Messages[0].Text.Should().Be(Segredo);
            memoria.LerTurnoAberto()!.Messages[0].Text.Should().Be(Segredo + " de novo");
        }

        [Fact]
        public void OsFatos_SaemCifrados_ESeEditamPelaTela()
        {
            var fatos = new FactStore(Memoria);
            fatos.Promote(new[]
            {
                new FactCandidate("k1", "- sobre o usuário: " + Segredo),
                new FactCandidate("k2", "- prefere respostas curtas")
            });

            Bruto(fatos.FactsPath).Should().NotContain("financeiro");
            Bruto(fatos.LedgerPath).Should().NotContain("financeiro");
            fatos.ReadFacts().Should().HaveCount(2);

            // A edição à mão de antes: o usuário apaga uma linha na aba Memória.
            string editado = string.Join(Environment.NewLine,
                fatos.Texto().Split('\n').Where(l => !l.Contains("financeiro")));
            fatos.Regravar(editado).Should().BeTrue();

            fatos.ReadFacts().Should().Equal("- prefere respostas curtas");
            Bruto(fatos.FactsPath).Should().NotContain("respostas curtas", "o arquivo regravado continua cifrado");

            // E o que ele apagou não volta.
            fatos.Promote(new[] { new FactCandidate("k1", "- sobre o usuário: " + Segredo) }).Should().Be(0);
        }

        [Fact]
        public void OsLembretes_SaemCifrados()
        {
            var lembretes = new Lembretes(_raiz);
            lembretes.Criar(DateTime.UtcNow.AddHours(1), "Ligar para o banco sobre o empréstimo.", "banco");

            Bruto(Path.Combine(_raiz, "lembretes.json")).Should().NotContain("empréstimo");
            new Lembretes(_raiz).Listar().Should().ContainSingle().Which.Texto.Should().Contain("empréstimo");
        }

        // ── Migração ───────────────────────────────────────────────────

        [Fact]
        public void Migrar_CifraOQueEstavaEmTextoClaro_SemPerderUmaLinha()
        {
            // É a única vez que o AIB reescreve um raw.jsonl: tem de abrir igual ao que era.
            string sessao = Path.Combine(Memoria, "sessions", "20261007-103424-278");
            Directory.CreateDirectory(sessao);
            string raw = Path.Combine(sessao, "raw.jsonl");
            string[] linhas =
            {
                "{\"Index\":0,\"AtUtc\":\"2026-10-07T13:00:00Z\",\"Messages\":[{\"Role\":\"user\",\"Text\":\"" + Segredo + "\"}],\"Artifacts\":[]}",
                "{\"Index\":1,\"AtUtc\":\"2026-10-07T13:01:00Z\",\"Messages\":[{\"Role\":\"user\",\"Text\":\"acentuação e \\\"aspas\\\"\"}],\"Artifacts\":[]}"
            };
            File.WriteAllLines(raw, linhas);
            File.WriteAllText(Path.Combine(Memoria, "facts.md"), "# Fatos duráveis\n\n- sobre o usuário: " + Segredo + "\n");
            File.WriteAllText(Path.Combine(_raiz, "chat_history.json"), "[\n  { \"Title\": \"" + Segredo + "\" }\n]");

            CifraDaMemoria.Migrar(_raiz).Should().Be(3);

            Bruto(raw).Should().NotContain("financeiro");
            Bruto(Path.Combine(Memoria, "facts.md")).Should().NotContain("financeiro");
            Bruto(Path.Combine(_raiz, "chat_history.json")).Should().NotContain("financeiro");

            ArquivoCifrado.Linhas(raw).Should().Equal(linhas);
            new SessionMemory("20261007-103424-278", Memoria).ReadTurns().Should().HaveCount(2);
            new FactStore(Memoria).ReadFacts().Should().Equal("- sobre o usuário: " + Segredo);
            ArquivoCifrado.Ler(Path.Combine(_raiz, "chat_history.json")).Should().Contain(Segredo);

            Directory.EnumerateFiles(_raiz, "*.cifrando", SearchOption.AllDirectories).Should().BeEmpty();
            CifraDaMemoria.Migrar(_raiz).Should().Be(0, "na segunda vez só confere");
        }

        [Fact]
        public void Migrar_AceitaArquivoComLinhasDosDoisTipos()
        {
            // Sessão aberta no dia da troca: linhas antigas em claro, novas já cifradas.
            var memoria = new SessionMemory("mista", Memoria);
            Directory.CreateDirectory(memoria.SessionDir);
            File.WriteAllText(memoria.RawPath,
                "{\"Index\":0,\"AtUtc\":\"2026-10-07T13:00:00Z\",\"Messages\":[{\"Role\":\"user\",\"Text\":\"antiga\"}],\"Artifacts\":[]}\n");
            memoria.AppendTurn(Turno(1, "nova"));

            memoria.ReadTurns().Should().HaveCount(2, "as duas convivem enquanto a migração não passa");

            CifraDaMemoria.Migrar(_raiz).Should().Be(1);

            Bruto(memoria.RawPath).Should().NotContain("antiga");
            memoria.ReadTurns().Select(t => t.Messages[0].Text).Should().Equal("antiga", "nova");
        }

        // ── Exportação ─────────────────────────────────────────────────

        [Fact]
        public void Exportar_EntregaTudoEmTextoClaro_ENaoMexeNoOriginal()
        {
            new SessionMemory("s1", Memoria).AppendTurn(Turno(0, Segredo));
            new FactStore(Memoria).Promote(new[] { new FactCandidate("k1", "- sobre o usuário: " + Segredo) });
            new Lembretes(_raiz).Criar(DateTime.UtcNow.AddHours(1), "Ligar para o banco.", "banco");

            string destino = Path.Combine(_raiz, "exportado");
            int arquivos = CifraDaMemoria.Exportar(destino, _raiz);

            arquivos.Should().BeGreaterThanOrEqualTo(4, "raw, fatos, índice e lembretes");
            Bruto(Path.Combine(destino, "memory", "sessions", "s1", "raw.jsonl")).Should().Contain(Segredo);
            Bruto(Path.Combine(destino, "memory", "facts.md")).Should().Contain(Segredo);
            Bruto(Path.Combine(destino, "lembretes.json")).Should().Contain("Ligar para o banco.");

            Bruto(Path.Combine(Memoria, "sessions", "s1", "raw.jsonl")).Should().NotContain("financeiro");
        }

        // ── A persona ainda lê a conversa antiga ───────────────────────

        [Fact]
        public async Task AFerramentaDeLeitura_AbreALinhaCifrada()
        {
            // Ler dentro de ~/.AIB/memory pede cartão (DadosProtegidos); passado o cartão, a
            // ferramenta tem de entregar o texto, e não a cifra.
            var memoria = new SessionMemory("s1", Memoria);
            memoria.AppendTurn(Turno(0, Segredo));

            // Raiz de dados em outro lugar: aqui o que se confere é a decifra, não o cartão.
            var ferramenta = new ReadFileTool(Path.Combine(_raiz, "outra-raiz"));
            string caminho = System.Text.Json.JsonSerializer.Serialize(new { path = memoria.RawPath });

            string lido = await ferramenta.ExecuteAsync(caminho);

            lido.Should().Contain(Segredo).And.NotContain(ArquivoCifrado.Marca);
        }
    }
}
