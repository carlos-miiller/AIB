using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Tools;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A pasta de dados do AIB vista pelas ferramentas de leitura.
    /// <para>
    /// <c>read</c>, <c>grep</c> e <c>glob</c> não pedem confirmação, e nada as impedia de entrar
    /// em <c>~/.AIB</c>: o modelo lia conversas antigas, a auditoria e os arquivos de e-mail sem
    /// ninguém ver. Só a <c>fs</c> tinha barreira, e para apagar.
    /// </para>
    /// <para>Tudo numa raiz temporária fazendo o papel do <c>~/.AIB</c>.</para>
    /// </summary>
    public class DadosProtegidosTests : IDisposable
    {
        private readonly string _base = Path.Combine(Path.GetTempPath(), "aib-dados-" + Guid.NewGuid().ToString("N"));
        private readonly string _dados;
        private readonly string _fora;

        public DadosProtegidosTests()
        {
            _dados = Path.Combine(_base, ".AIB");
            _fora = Path.Combine(_base, "projeto");

            Escrever(_dados, "credentials/openrouter.bin", "cifrado");
            Escrever(_dados, "profile.dat", "cifrado");
            Escrever(_dados, "navegador/perfil/Default/Cookies", "sessao");
            Escrever(_dados, "navegador/notas/site.md", "nota agulha");
            Escrever(_dados, "navegador/sites-liberados.txt", "exemplo.com");
            Escrever(_dados, "memory/sessions/s1/raw.jsonl", "conversa antiga agulha");
            Escrever(_dados, "logs/audit-2026-09-20.jsonl", "auditoria agulha");
            Escrever(_dados, "skills/planilha/SKILL.md", "manual agulha");
            Escrever(_dados, "character/Ellen/SOUL.MD", "alma");
            Escrever(_fora, "codigo.cs", "codigo agulha");
        }

        public void Dispose()
        {
            try { Directory.Delete(_base, recursive: true); } catch { }
        }

        private static void Escrever(string raiz, string relativo, string conteudo)
        {
            string caminho = Path.Combine(raiz, relativo.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
            File.WriteAllText(caminho, conteudo);
        }

        private string Em(string relativo) => Path.Combine(_dados, relativo.Replace('/', Path.DirectorySeparatorChar));

        private static string Args(object o) => JsonSerializer.Serialize(o);

        private sealed class Prompt : IConfirmationPrompt
        {
            public bool Permite { get; init; } = true;
            public CommandConfirmationContext? Visto { get; private set; }
            public int Perguntas { get; private set; }

            public Task<(bool Allowed, bool AlwaysAllow)> AskAsync(CommandConfirmationContext context)
            {
                Visto = context;
                Perguntas++;
                return Task.FromResult((Permite, true));
            }
        }

        private ToolRegistry Registry(IConfirmationPrompt? prompt)
        {
            var registry = new ToolRegistry(prompt);
            registry.Registrar(new ReadFileTool(_dados));
            registry.Registrar(new GrepTool(_dados));
            registry.Registrar(new GlobTool(_dados));
            return registry;
        }

        // ── As zonas ─────────────────────────────────────────────────────────

        [Theory]
        [InlineData("credentials/openrouter.bin", Acesso.Negado)]
        [InlineData("credentials", Acesso.Negado)]
        [InlineData("profile.dat", Acesso.Negado)]
        [InlineData("navegador/perfil/Default/Cookies", Acesso.Negado)]
        [InlineData("memory/sessions/s1/raw.jsonl", Acesso.Pergunta)]
        [InlineData("logs/audit-2026-09-20.jsonl", Acesso.Pergunta)]
        [InlineData("navegador/sites-liberados.txt", Acesso.Pergunta)]
        [InlineData("email/estado.json", Acesso.Pergunta)]
        [InlineData("", Acesso.Pergunta)]
        [InlineData("skills/planilha/SKILL.md", Acesso.Livre)]
        [InlineData("character/Ellen/SOUL.MD", Acesso.Livre)]
        [InlineData("navegador/notas/site.md", Acesso.Livre)]
        public void CadaParteDaPastaDeDados_TemOSeuAcesso(string relativo, Acesso esperado)
        {
            DadosProtegidos.Leitura(relativo.Length == 0 ? _dados : Em(relativo), _dados).Should().Be(esperado);
        }

        [Fact]
        public void ForaDaPastaDeDados_TudoELivre()
        {
            DadosProtegidos.Leitura(Path.Combine(_fora, "codigo.cs"), _dados).Should().Be(Acesso.Livre);
            DadosProtegidos.Leitura(_base, _dados).Should().Be(Acesso.Livre);

            // Vizinha com nome parecido não é a pasta de dados.
            DadosProtegidos.Leitura(_dados + "-copia\\credentials\\x.bin", _dados).Should().Be(Acesso.Livre);
        }

        [Fact]
        public void CaminhoComVoltas_NaoDesvia()
        {
            string torto = Path.Combine(_fora, "..", ".AIB", "skills", "..", "credentials", "openrouter.bin");

            DadosProtegidos.Leitura(torto, _dados).Should().Be(Acesso.Negado);
            DadosProtegidos.Leitura(Em("credentials/openrouter.bin").Replace('\\', '/').ToUpperInvariant(), _dados)
                .Should().Be(Acesso.Negado);
        }

        // ── read ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task Cofre_NaoEhLido_ENemPergunta()
        {
            var prompt = new Prompt();

            string r = await Registry(prompt).ExecuteToolAsync(
                Ferramentas.Ler, Args(new { path = Em("credentials/openrouter.bin") }), 10);

            r.Should().StartWith("ACESSO NEGADO");
            r.Should().NotContain("cifrado");
            prompt.Visto.Should().BeNull("o que é negado não vira pergunta");
        }

        [Fact]
        public async Task ConversaAntiga_SoComCartao_ESemSempre()
        {
            var prompt = new Prompt();
            string args = Args(new { path = Em("memory/sessions/s1/raw.jsonl") });
            var registry = Registry(prompt);

            string r = await registry.ExecuteToolAsync(Ferramentas.Ler, args, 10);

            r.Should().Contain("conversa antiga");
            prompt.Visto!.Command.Should().Be("LER " + Em("memory/sessions/s1/raw.jsonl"));
            prompt.Visto.SemSempre.Should().BeTrue();

            // O prompt respondeu "sempre permitir"; não vale aqui: a segunda leitura pergunta de novo.
            await registry.ExecuteToolAsync(Ferramentas.Ler, args, 10);
            prompt.Perguntas.Should().Be(2);
        }

        [Fact]
        public async Task ConversaAntiga_Recusada_OuSemInterface_NaoEhLida()
        {
            string args = Args(new { path = Em("memory/sessions/s1/raw.jsonl") });

            (await Registry(new Prompt { Permite = false }).ExecuteToolAsync(Ferramentas.Ler, args, 10))
                .Should().NotContain("conversa antiga");

            (await Registry(null).ExecuteToolAsync(Ferramentas.Ler, args, 10))
                .Should().StartWith("ACESSO NEGADO");

            // Chamada direta, por fora do registry: sem o cartão, recusa.
            (await new ReadFileTool(_dados).ExecuteAsync(args)).Should().StartWith("ACESSO NEGADO");
        }

        [Fact]
        public async Task SkillEArquivoDeFora_ContinuamSemPerguntar()
        {
            var registry = Registry(null);

            (await registry.ExecuteToolAsync(Ferramentas.Ler, Args(new { path = Em("skills/planilha/SKILL.md") }), 10))
                .Should().Contain("manual");
            (await registry.ExecuteToolAsync(Ferramentas.Ler, Args(new { path = Path.Combine(_fora, "codigo.cs") }), 10))
                .Should().Contain("codigo");
        }

        // ── grep e glob ──────────────────────────────────────────────────────

        [Fact]
        public async Task BuscaDeCima_NaoAtravessaAPastaDeDados()
        {
            // A raiz padrão das duas é a pasta do usuário, que CONTÉM ~/.AIB.
            var registry = Registry(null);

            string grep = await registry.ExecuteToolAsync(Ferramentas.Buscar, Args(new { pattern = "agulha", path = _base }), 10);
            string glob = await registry.ExecuteToolAsync(Ferramentas.Procurar, Args(new { pattern = "**/*", path = _base }), 10);

            grep.Should().Contain("codigo.cs").And.Contain("SKILL.md").And.Contain("site.md");
            grep.Should().NotContain("raw.jsonl").And.NotContain("audit-");

            glob.Should().Contain("codigo.cs").And.Contain("SOUL.MD");
            glob.Should().NotContain("raw.jsonl").And.NotContain("openrouter.bin").And.NotContain("Cookies")
                .And.NotContain("profile.dat");
        }

        [Fact]
        public async Task BuscaDentroDaPastaDeDados_PedeCartao_ENuncaTrazOCofre()
        {
            var prompt = new Prompt();
            var registry = Registry(prompt);

            string glob = await registry.ExecuteToolAsync(Ferramentas.Procurar, Args(new { pattern = "**/*", path = _dados }), 10);

            prompt.Visto!.Command.Should().Be("LISTAR ARQUIVOS DE " + _dados);
            glob.Should().Contain("raw.jsonl", "o usuário autorizou esta pasta");
            glob.Should().NotContain("openrouter.bin").And.NotContain("Cookies").And.NotContain("profile.dat");

            (await Registry(null).ExecuteToolAsync(Ferramentas.Buscar, Args(new { pattern = "agulha", path = Em("memory") }), 10))
                .Should().StartWith("ACESSO NEGADO");

            (await registry.ExecuteToolAsync(Ferramentas.Buscar, Args(new { pattern = "x", path = Em("credentials") }), 10))
                .Should().StartWith("ACESSO NEGADO");
        }
    }
}
