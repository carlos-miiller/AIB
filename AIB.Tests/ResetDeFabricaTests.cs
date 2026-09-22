using System;
using System.IO;
using System.Linq;
using AIB.Services;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O reset de fábrica limpando a memória e os arquivos de e-mail.
    /// <para>
    /// O diálogo mostrava a pasta <c>memory</c> como alvo e não encostava nela: quem lia
    /// acreditava ter apagado a memória, e ela continuava inteira. Agora apaga de verdade — e
    /// por isso as DUAS exceções passam a valer dinheiro. O <c>raw.jsonl</c> nunca é apagado
    /// (regra do projeto), e o <c>facts.md</c> é do usuário: os dois são renomeados.
    /// </para>
    /// <para>
    /// Tudo em raiz temporária. Um ensaio deste assunto que escapasse para o <c>~/.AIB</c> real
    /// apagaria a conversa de quem está rodando a suíte.
    /// </para>
    /// </summary>
    public class ResetDeFabricaTests : IDisposable
    {
        private readonly string _raiz =
            Path.Combine(Path.GetTempPath(), "aib-reset-" + Guid.NewGuid().ToString("N"));

        private static readonly DateTime Quando = new(2026, 9, 22, 14, 30, 0);
        private const string Carimbo = "20260922-1430";

        public void Dispose()
        {
            try { if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true); } catch { }
        }

        /// <summary>Uma raiz de dados com a cara da de verdade.</summary>
        private void Povoar(string sessao = "20260920-101500-123")
        {
            Escrever($"memory/sessions/{sessao}/raw.jsonl", "{\"turno\":1}");
            Escrever($"memory/sessions/{sessao}/chapters.jsonl", "{\"cap\":1}");
            Escrever($"memory/sessions/{sessao}/acts.jsonl", "{\"ato\":1}");
            Escrever($"memory/sessions/{sessao}/turno-aberto.json", "{}");
            Escrever($"memory/sessions/{sessao}/compactacao.log", "compactou");
            Escrever("memory/facts.md", "# fatos\n- mora em Curitiba");
            Escrever("memory/facts.index.jsonl", "{\"fato\":\"x\"}");

            Escrever("email/estado.json", "{}");
            Escrever("email/vigias.json", "[]");
            Escrever("email/regras.md", "regras");
            Escrever("email/diario/diario-2026-09-20.json", "[]");
            Escrever("email/conversas/abc/triagem.jsonl", "{}");

            // Fora do alcance: quem cuida deles é outro pedaço do reset, ou ninguém.
            Escrever("profile.dat", "cifrado");
            Escrever("chat_history.json", "[]");
            Escrever("credentials/openrouter.bin", "cifrado");
            Escrever("logs/audit-2026-09-20.jsonl", "{}");
            Escrever("skills/planilha/SKILL.md", "# skill");
            Escrever("character/Ayano/SOUL.MD", "# alma");
        }

        private void Escrever(string relativo, string conteudo)
        {
            string caminho = Path.Combine(_raiz, relativo.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
            File.WriteAllText(caminho, conteudo);
        }

        private bool Existe(string relativo) =>
            File.Exists(Path.Combine(_raiz, relativo.Replace('/', Path.DirectorySeparatorChar)));

        private string[] NomesEm(string relativo)
        {
            string pasta = Path.Combine(_raiz, relativo.Replace('/', Path.DirectorySeparatorChar));
            return Directory.Exists(pasta)
                ? Directory.GetFiles(pasta).Select(c => Path.GetFileName(c)!).ToArray()
                : Array.Empty<string>();
        }

        // ─────────────────────────────────────────────────────────────────────
        // O que fica
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ORAW_JSONL_VIRA_BAK_ENuncaEhApagado()
        {
            // A regra mais dura do projeto. Resumo é perda irreversível, e um resumo errado aqui
            // não gera só incoerência: gera um agente agindo sobre informação errada.
            Povoar();

            ResetDeFabrica.Limpar(_raiz, Quando);

            Existe("memory/sessions/20260920-101500-123/raw.jsonl").Should().BeFalse();
            Existe($"memory/sessions/20260920-101500-123/raw.{Carimbo}.jsonl.bak").Should().BeTrue();

            File.ReadAllText(Path.Combine(_raiz, "memory", "sessions", "20260920-101500-123",
                                          $"raw.{Carimbo}.jsonl.bak"))
                .Should().Be("{\"turno\":1}", "renomear não é reescrever");
        }

        [Fact]
        public void OFACTS_MD_VIRA_BAK_PorqueEhTextoDoUsuario()
        {
            // O facts.md é escrito e reordenado à mão. "Voltar ao estado de fábrica" não pode
            // querer dizer "perder o que você escreveu".
            Povoar();

            ResetDeFabrica.Limpar(_raiz, Quando);

            Existe("memory/facts.md").Should().BeFalse();
            Existe($"memory/facts.{Carimbo}.md.bak").Should().BeTrue();
        }

        [Fact]
        public void UmSEGUNDO_Reset_NAO_ApagaOBakDoPrimeiro()
        {
            // Preservar uma vez e apagar na vez seguinte seria a regra durando um reset.
            Povoar();

            ResetDeFabrica.Limpar(_raiz, Quando);
            ResetDeFabrica.Limpar(_raiz, Quando.AddDays(1));

            NomesEm("memory/sessions/20260920-101500-123")
                .Should().BeEquivalentTo(new[] { $"raw.{Carimbo}.jsonl.bak" });
        }

        [Fact]
        public void DoisResetsNoMESMO_Minuto_NaoSeAtropelam()
        {
            // O carimbo tem resolução de minuto; um File.Move por cima apagaria o cru salvo há
            // trinta segundos.
            Escrever("memory/sessions/a/raw.jsonl", "primeira");
            ResetDeFabrica.Limpar(_raiz, Quando);

            Escrever("memory/sessions/a/raw.jsonl", "segunda");
            ResetDeFabrica.Limpar(_raiz, Quando);

            NomesEm("memory/sessions/a").Should().HaveCount(2, "nenhum dos dois pode ter sumido");
        }

        // ─────────────────────────────────────────────────────────────────────
        // O que some
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void OsRESUMOS_ECompanhia_SOMEM()
        {
            Povoar();

            ResetDeFabrica.Limpar(_raiz, Quando);

            NomesEm("memory/sessions/20260920-101500-123")
                .Should().BeEquivalentTo(new[] { $"raw.{Carimbo}.jsonl.bak" },
                                         "capítulos, atos, turno aberto e diário da compactação saem");

            Existe("memory/facts.index.jsonl").Should().BeFalse("é da máquina, não do usuário");
        }

        [Fact]
        public void APastaDeEMAIL_SOME_Inteira()
        {
            // Nada em email/ é do usuário nem é insubstituível: tudo volta na próxima passada do
            // vigia, lendo o servidor.
            Povoar();

            ResetDeFabrica.Limpar(_raiz, Quando);

            Directory.Exists(Path.Combine(_raiz, "email")).Should().BeFalse();
        }

        [Fact]
        public void SessaoQueFicouVAZIA_SaiJunto()
        {
            // Uma conversa que nunca fechou turno não tem raw.jsonl. A pasta dela vira ruído.
            Escrever("memory/sessions/vazia/turno-aberto.json", "{}");

            ResetDeFabrica.Limpar(_raiz, Quando);

            Directory.Exists(Path.Combine(_raiz, "memory", "sessions", "vazia")).Should().BeFalse();
        }

        // ─────────────────────────────────────────────────────────────────────
        // O que não se toca
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void NADA_ForaDeMemoryEEmail_EhTocado()
        {
            // Os logs ficam: auditoria que o reset apaga não é auditoria. O resto tem dono —
            // credenciais, configurações e histórico são apagados por quem cuida deles.
            Povoar();

            ResetDeFabrica.Limpar(_raiz, Quando);

            Existe("profile.dat").Should().BeTrue();
            Existe("chat_history.json").Should().BeTrue();
            Existe("credentials/openrouter.bin").Should().BeTrue();
            Existe("logs/audit-2026-09-20.jsonl").Should().BeTrue();
            Existe("skills/planilha/SKILL.md").Should().BeTrue();
            Existe("character/Ayano/SOUL.MD").Should().BeTrue();
        }

        [Fact]
        public void NADA_ForaDaRAIZ_EhTocado()
        {
            // A raiz é parâmetro justamente para isto poder ser provado.
            string vizinha = _raiz + "-vizinha";
            Directory.CreateDirectory(Path.Combine(vizinha, "memory"));
            File.WriteAllText(Path.Combine(vizinha, "memory", "raw.jsonl"), "de outro");

            try
            {
                Povoar();
                ResetDeFabrica.Limpar(_raiz, Quando);

                File.Exists(Path.Combine(vizinha, "memory", "raw.jsonl")).Should().BeTrue();
            }
            finally
            {
                try { Directory.Delete(vizinha, true); } catch { }
            }
        }

        [Fact]
        public void RaizQueNAO_EXISTE_NaoEhErro()
        {
            Action limpar = () => ResetDeFabrica.Limpar(Path.Combine(_raiz, "nunca-existiu"), Quando);

            limpar.Should().NotThrow();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Quando o disco diz não
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ArquivoTRAVADO_NaoDerruba_NemABORTA_ORestante()
        {
            // Um arquivo em uso derrubaria o reset pela metade — e a metade que morre é sempre a
            // que ainda não rodou. A falha vira linha no console e o resto segue.
            Povoar();

            string travado = Path.Combine(_raiz, "memory", "sessions", "20260920-101500-123",
                                          "chapters.jsonl");

            using (new FileStream(travado, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Action limpar = () => ResetDeFabrica.Limpar(_raiz, Quando);
                limpar.Should().NotThrow();
            }

            Existe($"memory/sessions/20260920-101500-123/raw.{Carimbo}.jsonl.bak")
                .Should().BeTrue("o que vem depois do arquivo travado ainda tem de acontecer");
            Existe($"memory/facts.{Carimbo}.md.bak").Should().BeTrue();
            Directory.Exists(Path.Combine(_raiz, "email")).Should().BeFalse();
        }

        [Fact]
        public void OResultado_CONTA_OQueFezEOQueNaoConseguiu()
        {
            // A auditoria grava estes números: sem eles, "reset concluído" é uma frase sem prova.
            Povoar();

            var r = ResetDeFabrica.Limpar(_raiz, Quando);

            r.Preservados.Should().Be(2, "raw.jsonl e facts.md");
            r.Apagados.Should().Be(10, "cinco de memory e cinco de email");
            r.Falhas.Should().Be(0);
        }

        [Fact]
        public void ONomeDoBAK_PoeOCarimboANTES_DaExtensao()
        {
            // É a promessa literal do diálogo. "raw.jsonl.20260922-1430.bak" cumpriria a regra e
            // deixaria de se ler como o que é.
            ResetDeFabrica.NomeDoBackup("raw.jsonl", Carimbo).Should().Be($"raw.{Carimbo}.jsonl.bak");
            ResetDeFabrica.NomeDoBackup("facts.md", Carimbo).Should().Be($"facts.{Carimbo}.md.bak");
        }
    }
}
