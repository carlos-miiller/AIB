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
    /// A ferramenta de arquivo e pasta. O que está travado aqui é o que faz ela valer a pena em
    /// vez do shell: o cartão diz o tamanho do estrago antes, o piso barra apagar pasta cheia
    /// abaixo do Nível 7, nada é sobrescrito, e o que foi aprovado é o que executa.
    /// </summary>
    [Collection("Escrita")]
    public class FsToolTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "aib-fs-" + Guid.NewGuid().ToString("N"));
        private readonly FsTool _fs = new();

        public FsToolTests()
        {
            Directory.CreateDirectory(_dir);
            PastasSemConfirmacao.Configurar("");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static string Args(object o) => JsonSerializer.Serialize(o);

        private string PastaCheia(string nome, int arquivos)
        {
            string p = Path.Combine(_dir, nome);
            Directory.CreateDirectory(p);
            for (int i = 0; i < arquivos; i++) File.WriteAllText(Path.Combine(p, $"a{i}.txt"), new string('x', 100));
            return p;
        }

        private sealed class Prompt : IConfirmationPrompt
        {
            public bool Permite { get; init; } = true;
            public int Perguntas { get; private set; }
            public CommandConfirmationContext? Visto { get; private set; }

            public Task<(bool Allowed, bool AlwaysAllow)> AskAsync(CommandConfirmationContext context)
            {
                Perguntas++;
                Visto = context;
                return Task.FromResult((Permite, false));
            }
        }

        [Fact]
        public void OCartao_DizQuantosArquivosEQuantoPesam()
        {
            string p = PastaCheia("trabalho", 3);
            var ctx = _fs.BuildConfirmationContext(Args(new { action = "delete", path = p }), 9)!;

            ctx.Command.Should().StartWith("APAGAR PASTA " + p).And.Contain("3 arquivo(s)").And.Contain("300 B");
        }

        [Fact]
        public void ApagarPastaCheia_AbaixoDoNivel7_EhBarradoSemPerguntar_ENoNivel7Pergunta()
        {
            // O shell barra Remove-Item -Recurse abaixo do 7. Sem o piso tipado, esta ferramenta
            // seria o caminho por baixo da regra.
            string p = PastaCheia("cheia", 2);
            string a = Args(new { action = "delete", path = p });

            var prompt = new Prompt();
            var registry = new ToolRegistry(prompt);

            registry.ExecuteToolAsync(Ferramentas.Arquivos, a, 2).Result.Should().StartWith("ACESSO NEGADO (FLOOR)");
            prompt.Perguntas.Should().Be(0);
            Directory.Exists(p).Should().BeTrue();

            ITool fs = _fs;
            fs.PisoTipado(_fs.BuildConfirmationContext(a, 7)!, 7).Should().BeNull("no Nível 7 decide o cartão");
        }

        [Fact]
        public void PastaVazia_NaoPassaPeloPiso()
        {
            string p = PastaCheia("vazia", 0);
            ITool fs = _fs;
            var ctx = _fs.BuildConfirmationContext(Args(new { action = "delete", path = p }), 2)!;

            ctx.Command.Should().EndWith("(vazia)");
            fs.PisoTipado(ctx, 2).Should().BeNull();
        }

        [Fact]
        public async Task CriarPasta_Copiar_Mover_Renomear_FuncionamComAutorizacao()
        {
            var registry = new ToolRegistry(new Prompt());
            string nova = Path.Combine(_dir, "nova");

            (await registry.ExecuteToolAsync(Ferramentas.Arquivos, Args(new { action = "mkdir", path = nova }), 2))
                .Should().StartWith("SUCESSO");
            Directory.Exists(nova).Should().BeTrue();

            string arq = Path.Combine(_dir, "x.txt");
            File.WriteAllText(arq, "oi");
            string copia = Path.Combine(nova, "x.txt");
            (await registry.ExecuteToolAsync(Ferramentas.Arquivos, Args(new { action = "copy", path = arq, destination = copia }), 2))
                .Should().StartWith("SUCESSO");
            File.ReadAllText(copia).Should().Be("oi");

            (await registry.ExecuteToolAsync(Ferramentas.Arquivos, Args(new { action = "rename", path = copia, new_name = "y.txt" }), 2))
                .Should().StartWith("SUCESSO");
            File.Exists(Path.Combine(nova, "y.txt")).Should().BeTrue();

            string movido = Path.Combine(_dir, "movido.txt");
            (await registry.ExecuteToolAsync(Ferramentas.Arquivos, Args(new { action = "move", path = arq, destination = movido }), 2))
                .Should().StartWith("SUCESSO");
            File.Exists(arq).Should().BeFalse();
            File.Exists(movido).Should().BeTrue();
        }

        [Fact]
        public async Task NadaEhSobrescrito_EDestinoDentroDaOrigemEhRecusado()
        {
            string a = Path.Combine(_dir, "a.txt"), b = Path.Combine(_dir, "b.txt");
            File.WriteAllText(a, "a");
            File.WriteAllText(b, "b");
            var prompt = new Prompt();
            var registry = new ToolRegistry(prompt);

            (await registry.ExecuteToolAsync(Ferramentas.Arquivos, Args(new { action = "copy", path = a, destination = b }), 2))
                .Should().StartWith("ERRO").And.Contain("nunca sobrescreve");
            File.ReadAllText(b).Should().Be("b");

            string p = PastaCheia("origem", 1);
            _fs.Validar(Args(new { action = "copy", path = p, destination = Path.Combine(p, "dentro") }))
                .Should().Contain("dentro dela mesma");
            prompt.Perguntas.Should().Be(0, "o que não pode dar certo não vira pergunta");
        }

        [Fact]
        public async Task RecusaDoUsuario_NaoApagaNada()
        {
            string arq = Path.Combine(_dir, "fica.txt");
            File.WriteAllText(arq, "fica");
            var registry = new ToolRegistry(new Prompt { Permite = false });

            (await registry.ExecuteToolAsync(Ferramentas.Arquivos, Args(new { action = "delete", path = arq }), 9))
                .Should().Be(ToolRegistry.RecusaDoUsuario);
            File.Exists(arq).Should().BeTrue();
        }

        [Fact]
        public async Task AlvoQueMudouEntreOCartaoEAExecucao_NaoExecuta()
        {
            string p = PastaCheia("mexida", 1);
            string a = Args(new { action = "delete", path = p });
            var autorizado = _fs.BuildConfirmationContext(a, 9)!;

            File.WriteAllText(Path.Combine(p, "novo.txt"), "chegou depois");

            (await _fs.ExecutarAutorizadoAsync(a, 9, autorizado)).Should().StartWith("ERRO: o alvo mudou");
            Directory.Exists(p).Should().BeTrue();
            (await _fs.ExecutarAutorizadoAsync(a, 9, null)).Should().StartWith("ERRO");
        }

        [Fact]
        public void RaizesEDadosDoAIB_NaoSeApagam()
        {
            _fs.Validar(Args(new { action = "delete", path = @"C:\" })).Should().Contain("raiz");
            _fs.Validar(Args(new { action = "delete", path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }))
                .Should().Contain("pasta de usuário");
            _fs.Validar(Args(new { action = "delete", path = DirectoryService.MemoryDir }))
                .Should().Contain("dados do AIB");
        }

        [Fact]
        public void SoCriarPasta_EhDispensadoNasPastasSemConfirmacao()
        {
            // A dispensa foi pensada para gravar e editar. Aqui o estrago é a árvore inteira.
            PastasSemConfirmacao.Configurar(_dir);
            _fs.DispensaConfirmacao(Args(new { action = "mkdir", path = Path.Combine(_dir, "n") })).Should().BeTrue();

            string arq = Path.Combine(_dir, "z.txt");
            File.WriteAllText(arq, "z");
            PastasSemConfirmacao.Configurar(_dir);
            _fs.DispensaConfirmacao(Args(new { action = "delete", path = arq })).Should().BeFalse();
            PastasSemConfirmacao.Configurar("");
        }

        [Fact]
        public void ADescricao_CabeNoOrcamento()
        {
            // Paga em toda requisição. Uma ferramenta com 'action', e não cinco, foi a decisão
            // de economia; a descrição tem de acompanhar.
            _fs.Description.Length.Should().BeLessThanOrEqualTo(220);
        }
    }
}
