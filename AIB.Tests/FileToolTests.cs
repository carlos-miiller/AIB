using System;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    /// A ferramenta 'file': gravar, editar e organizar numa ferramenta só, com lote.
    /// <para>
    /// Caso real (29/10/2026): para organizar C:\Users\Carlo\Ellen, a agente criou 4 subpastas e
    /// moveu ~30 arquivos, uma chamada por vez — 98 de 98 respostas do dia vieram com uma
    /// ferramenta só, e cada uma era uma volta inteira ao modelo. Com 'ops', o lote vai numa
    /// chamada e num cartão, roda em ordem e para na primeira falha.
    /// </para>
    /// </summary>
    [Collection("Escrita")]
    public class FileToolTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "aib-file-" + Guid.NewGuid().ToString("N"));
        private readonly FileTool _file = new();

        public FileToolTests()
        {
            Directory.CreateDirectory(_dir);
            PastasSemConfirmacao.Configurar("");
        }

        public void Dispose()
        {
            PastasSemConfirmacao.Configurar("");
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static string Args(object o) => JsonSerializer.Serialize(o);

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

        private string Arquivo(string nome, string texto = "x")
        {
            string p = Path.Combine(_dir, nome);
            File.WriteAllText(p, texto);
            return p;
        }

        [Fact]
        public async Task Lote_CriaAPastaEMoveParaEla_NumCartaoSo()
        {
            // O caso da Ellen em miniatura: a pasta nasce na operação 1, e as seguintes movem
            // para dentro dela. O destino das mudanças não existe quando o cartão é montado.
            string a = Arquivo("relatorio.md"), b = Arquivo("comunicado.md"), c = Arquivo("campanha.jpeg");
            string sub = Path.Combine(_dir, "01-incidentes");

            var prompt = new Prompt();
            var registry = new ToolRegistry(prompt);

            string resultado = await registry.ExecuteToolAsync(Ferramentas.Arquivo, Args(new
            {
                ops = new object[]
                {
                    new { action = "mkdir", path = sub },
                    new { action = "move", path = a, destination = Path.Combine(sub, "relatorio.md") },
                    new { action = "move", path = b, destination = Path.Combine(sub, "comunicado.md") },
                    new { action = "move", path = c, destination = Path.Combine(sub, "campanha.jpeg") }
                }
            }), 2);

            resultado.Should().StartWith("SUCESSO: as 4 operações");
            prompt.Perguntas.Should().Be(1, "um lote, um cartão");
            prompt.Visto!.Tool.Should().Be(Ferramentas.Arquivo);
            FileTool.LinhasDoLote(prompt.Visto.Command).Should().HaveCount(4);
            Directory.GetFiles(sub).Should().HaveCount(3);
            File.Exists(a).Should().BeFalse();
        }

        [Fact]
        public async Task Lote_ParaNaPrimeiraFalha_EDizOQueFicouFeito()
        {
            string a = Arquivo("a.txt"), b = Arquivo("b.txt");
            string sub = Path.Combine(_dir, "sub");
            Directory.CreateDirectory(sub);
            var registry = new ToolRegistry(new Prompt());

            // A operação 2 move 'a' de novo (já não existe depois da 1): falha na execução.
            string resultado = await registry.ExecuteToolAsync(Ferramentas.Arquivo, Args(new
            {
                ops = new object[]
                {
                    new { action = "move", path = a, destination = Path.Combine(sub, "a.txt") },
                    new { action = "move", path = a, destination = Path.Combine(sub, "a2.txt") },
                    new { action = "move", path = b, destination = Path.Combine(sub, "b.txt") }
                }
            }), 2);

            resultado.Should().StartWith("ERRO: o lote parou na operação 2 de 3; 1 feita(s)");
            resultado.Should().Contain("[3/3] ERRO: não executada");
            File.Exists(Path.Combine(sub, "a.txt")).Should().BeTrue("a operação 1 continua feita");
            File.Exists(b).Should().BeTrue("a 3 não rodou");
        }

        [Fact]
        public async Task Lote_ComOperacaoImpossivel_NaoViraCartao_ENadaRoda()
        {
            string a = Arquivo("a.txt"), b = Arquivo("b.txt");
            var prompt = new Prompt();
            var registry = new ToolRegistry(prompt);

            string resultado = await registry.ExecuteToolAsync(Ferramentas.Arquivo, Args(new
            {
                ops = new object[]
                {
                    new { action = "rename", path = a, new_name = "a2.txt" },
                    new { action = "copy", path = b, destination = Path.Combine(_dir, "nao-existe", "b.txt") }
                }
            }), 2);

            resultado.Should().StartWith("ERRO: operação 2 de 2 (copy)").And.Contain("Nada do lote foi feito");
            prompt.Perguntas.Should().Be(0);
            File.Exists(a).Should().BeTrue();
        }

        [Fact]
        public async Task Lote_NaPastaSemConfirmacao_NaoPergunta_EComUmaOperacaoForaPergunta()
        {
            string confianca = Path.Combine(_dir, "Ellen");
            Directory.CreateDirectory(confianca);
            string a = Path.Combine(confianca, "a.txt");
            File.WriteAllText(a, "a");
            PastasSemConfirmacao.Configurar(confianca);

            var prompt = new Prompt();
            var registry = new ToolRegistry(prompt);
            string sub = Path.Combine(confianca, "sub");

            (await registry.ExecuteToolAsync(Ferramentas.Arquivo, Args(new
            {
                ops = new object[]
                {
                    new { action = "mkdir", path = sub },
                    new { action = "move", path = a, destination = Path.Combine(sub, "a.txt") },
                    new { action = "write", path = Path.Combine(sub, "leia.md"), content = "oi" }
                }
            }), 2)).Should().StartWith("SUCESSO");
            prompt.Perguntas.Should().Be(0);

            _file.DispensaConfirmacao(Args(new
            {
                ops = new object[]
                {
                    new { action = "write", path = Path.Combine(sub, "b.md"), content = "b" },
                    new { action = "write", path = Path.Combine(_dir, "fora.md"), content = "f" }
                }
            })).Should().BeFalse("uma operação fora da pasta leva o lote ao cartão");
        }

        [Fact]
        public async Task Lote_QueApagaPastaCheia_AbaixoDoNivel7_EhBarradoPeloPiso()
        {
            string cheia = Path.Combine(_dir, "cheia");
            Directory.CreateDirectory(cheia);
            File.WriteAllText(Path.Combine(cheia, "x.txt"), "x");
            string a = Arquivo("a.txt");
            var prompt = new Prompt();
            var registry = new ToolRegistry(prompt);

            string resultado = await registry.ExecuteToolAsync(Ferramentas.Arquivo, Args(new
            {
                ops = new object[]
                {
                    new { action = "rename", path = a, new_name = "b.txt" },
                    new { action = "delete", path = cheia }
                }
            }), 2);

            resultado.Should().StartWith("ACESSO NEGADO (FLOOR)");
            prompt.Perguntas.Should().Be(0);
            File.Exists(a).Should().BeTrue();
        }

        [Fact]
        public async Task Lote_AlvoQueMudouDepoisDoCartao_NaoExecuta()
        {
            string pasta = Path.Combine(_dir, "pasta");
            Directory.CreateDirectory(pasta);
            File.WriteAllText(Path.Combine(pasta, "1.txt"), "1");
            string a = Arquivo("a.txt");

            string args = Args(new
            {
                ops = new object[]
                {
                    new { action = "rename", path = a, new_name = "b.txt" },
                    new { action = "move", path = pasta, destination = Path.Combine(_dir, "movida") }
                }
            });
            var autorizado = _file.BuildConfirmationContext(args, 9)!;

            File.WriteAllText(Path.Combine(pasta, "chegou-depois.txt"), "novo");

            string resultado = await _file.ExecutarAutorizadoAsync(args, 9, autorizado);
            resultado.Should().Contain("[2/2] ERRO: o alvo mudou");
            Directory.Exists(pasta).Should().BeTrue();
        }

        [Fact]
        public async Task Lote_EditaDuasVezesOMesmoArquivo_EmOrdem()
        {
            // A segunda edição procura o texto que a primeira escreveu: não dá para conferir no
            // disco antes, só na hora.
            string p = Arquivo("nota.txt", "olá mundo");
            var registry = new ToolRegistry(new Prompt());

            (await registry.ExecuteToolAsync(Ferramentas.Arquivo, Args(new
            {
                ops = new object[]
                {
                    new { action = "edit", path = p, old_string = "olá", new_string = "tchau" },
                    new { action = "edit", path = p, old_string = "tchau mundo", new_string = "tchau, mundo" }
                }
            }), 2)).Should().StartWith("SUCESSO");

            File.ReadAllText(p).Should().Be("tchau, mundo");
        }

        [Fact]
        public void UmaOperacao_TemOCartaoDaFerramentaAntiga()
        {
            string p = Path.Combine(_dir, "novo.txt");
            var ctx = _file.BuildConfirmationContext(Args(new { action = "write", path = p, content = "oi" }), 2)!;

            ctx.Tool.Should().Be(Ferramentas.Gravar);
            ctx.Command.Should().Be("CRIAR " + p);
            ctx.ScriptBody.Should().Be("oi");
        }

        [Fact]
        public void MandarOpsEActionJuntos_EhRecusado()
        {
            _file.Validar(Args(new { action = "write", path = "C:\\x.txt", ops = new object[] { new { action = "mkdir", path = "C:\\y" } } }))
                .Should().Contain("'ops' OU action/path");
        }

        [Fact]
        public void OSchema_TemFile_EOsMotoresFicamFora_MasContinuamRegistrados()
        {
            var registry = new ToolRegistry();
            var nomes = registry.GetActiveTools(9).Select(t => t.FunctionName).ToList();

            nomes.Should().Contain(Ferramentas.Arquivo);
            nomes.Should().NotContain(new[] { Ferramentas.Gravar, Ferramentas.Editar, Ferramentas.Arquivos });

            // O histórico antigo chama write/edit/fs: continuam passando pelo portão.
            registry.Contains(Ferramentas.Gravar).Should().BeTrue();
            registry.Contains(Ferramentas.Arquivos).Should().BeTrue();
        }

        [Fact]
        public void Abrir_DaUmResultadoPorOperacao_EAMemoriaVeCadaUma()
        {
            string args = Args(new
            {
                ops = new object[]
                {
                    new { action = "write", path = @"C:\t\a.md", content = "a" },
                    new { action = "move", path = @"C:\t\b.md", destination = @"C:\t\s\b.md" },
                    new { action = "edit", path = @"C:\t\c.md", old_string = "x", new_string = "y" }
                }
            });
            string resultado = "ERRO: o lote parou na operação 2 de 3; 1 feita(s) antes dela, que continuam feitas.\n"
                               + "[1/3] SUCESSO: Arquivo salvo corretamente em 'C:\\t\\a.md'.\n"
                               + "[2/3] ERRO: 'C:\\t\\b.md' não existe.\n"
                               + "[3/3] ERRO: não executada — a operação 2 falhou antes.";

            var abertas = FileTool.Abrir(Ferramentas.Arquivo, args, resultado);
            abertas.Select(a => a.Nome).Should().Equal(Ferramentas.Gravar, Ferramentas.Arquivos, Ferramentas.Editar);
            abertas[0].Resultado.Should().StartWith("SUCESSO");
            ArtifactExtractor.Falhou(abertas[1].Resultado).Should().BeTrue();

            var mensagens = new ChatMessage[]
            {
                ChatMessage.CreateUserMessage("organize"),
                new AssistantChatMessage(new[] { ChatToolCall.CreateFunctionToolCall("c1", Ferramentas.Arquivo, BinaryData.FromString(args)) }),
                ChatMessage.CreateToolMessage("c1", resultado)
            };
            var artefatos = ArtifactExtractor.Extract(mensagens);
            artefatos.Should().HaveCount(2, "write e edit deixam artefato; o fs fica no Estado");
            artefatos[0].Failed.Should().BeFalse();
            artefatos[1].Failed.Should().BeTrue();
        }

        [Fact]
        public void Recusa_DoLote_ValeParaTodasAsOperacoes()
        {
            string args = Args(new
            {
                ops = new object[]
                {
                    new { action = "mkdir", path = @"C:\t\x" },
                    new { action = "write", path = @"C:\t\x\a.md", content = "a" }
                }
            });

            FileTool.Abrir(Ferramentas.Arquivo, args, ToolRegistry.RecusaDoUsuario)
                .Should().OnlyContain(a => ArtifactExtractor.Recusado(a.Resultado));
        }

        [Fact]
        public void UmaOperacao_AparecePeloNomeAntigo()
        {
            var (nome, args) = FileTool.Simples(Ferramentas.Arquivo, Args(new { action = "edit", path = @"C:\a.txt", old_string = "a", new_string = "b" }));
            nome.Should().Be(Ferramentas.Editar);
            ArtifactExtractor.TrocaDaEdicao(Ferramentas.Arquivo, Args(new { action = "edit", path = @"C:\a.txt", old_string = "a", new_string = "b" }))
                .Should().NotBeNull();
            ArtifactExtractor.ResumirArgumento(Ferramentas.Arquivo, Args(new { ops = new object[]
            {
                new { action = "mkdir", path = @"C:\t\x" },
                new { action = "move", path = @"C:\t\a.md", destination = @"C:\t\x\a.md" }
            } })).Should().Be("2 operações: mkdir x, move a.md");
        }
    }
}
