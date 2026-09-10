using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Tools;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// As ferramentas que faltavam: <c>edit</c>, <c>glob</c> e <c>grep</c>.
    /// <para>
    /// A ausência das três era cara de um jeito invisível. Sem <c>glob</c>, o modelo reinventa a
    /// busca pelo shell — dois turnos e seis minutos gastos em <c>Get-ChildItem</c> numa sessão
    /// real. Sem <c>edit</c>, trocar três campos de um HTML de duzentas linhas obriga a gerar as
    /// duzentas de novo, e foi por isso que ele fugiu para o PowerShell e quebrou a sintaxe duas
    /// vezes seguidas.
    /// </para>
    /// </summary>
    public class FerramentasDeArquivoTests : IDisposable
    {
        private readonly string _dir;

        public FerramentasDeArquivoTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "aib-arq-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string Criar(string nome, string conteudo)
        {
            string caminho = Path.Combine(_dir, nome);
            Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
            File.WriteAllText(caminho, conteudo);
            return caminho;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Nomes e rótulos
        // ─────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("read", "Lendo arquivo")]
        [InlineData("edit", "Editando arquivo")]
        [InlineData("glob", "Procurando arquivos")]
        [InlineData("grep", "Buscando no conteúdo")]
        [InlineData("shell", "Executando comando")]
        public void ORotulo_DIZ_OQueEstaAcontecendo(string nome, string esperado)
        {
            // O modelo lê o nome curto; a PESSOA lê o rótulo. "read" é endereço, "Lendo arquivo"
            // é notícia — e é o que aparece na trilha de ações enquanto a ferramenta roda.
            Ferramentas.Rotulo(nome).Should().Be(esperado);
        }

        [Fact]
        public void NomeDesconhecido_DEVOLVE_OProprioNome()
        {
            // Habilidade instalada pelo usuário não tem rótulo cadastrado, e mostrar o nome dela
            // é melhor que mostrar vazio.
            Ferramentas.Rotulo("ler-planilha").Should().Be("ler-planilha");
            Ferramentas.Rotulo(null).Should().BeEmpty();
        }

        // ─────────────────────────────────────────────────────────────────────
        // edit
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task OEdit_TROCA_SoOTrecho()
        {
            string arquivo = Criar("assinatura.html",
                "<p>Nome: Carlos</p>\n<p>Email: carlos@x.com</p>\n<p>Cargo: TI</p>");

            var tool = new EditFileTool();
            string args = JsonSerializer.Serialize(new
            {
                path = arquivo,
                old_string = "Carlos",
                new_string = "Ana"
            });

            tool.Validar(args).Should().BeNull();
            (await tool.ExecuteAsync(args, 9)).Should().StartWith("SUCESSO");

            string depois = File.ReadAllText(arquivo);
            depois.Should().Contain("Nome: Ana");
            depois.Should().Contain("carlos@x.com", "o resto do arquivo não foi tocado");
            depois.Should().Contain("Cargo: TI");
        }

        [Fact]
        public void OTrechoAMBIGUO_EhRecusado_AntesDeEditar()
        {
            // Editar a primeira de cinco ocorrências é o erro que ninguém percebe até o arquivo
            // estar errado. A falha é preferível ao estrago.
            string arquivo = Criar("repetido.txt", "cor: azul\nfundo cor: azul\nborda cor: azul");

            string? recusa = new EditFileTool().Validar(JsonSerializer.Serialize(new
            {
                path = arquivo,
                old_string = "cor: azul",
                new_string = "cor: verde"
            }));

            recusa.Should().NotBeNull();
            recusa.Should().Contain("3 vezes");
            recusa.Should().Contain("replace_all", "a saída é nomeada, não só o problema");
        }

        [Fact]
        public async Task ComReplaceAll_ATroca_ValeParaTodas()
        {
            string arquivo = Criar("repetido.txt", "azul\nazul\nazul");

            string args = JsonSerializer.Serialize(new
            {
                path = arquivo,
                old_string = "azul",
                new_string = "verde",
                replace_all = true
            });

            var tool = new EditFileTool();
            tool.Validar(args).Should().BeNull();
            await tool.ExecuteAsync(args, 9);

            File.ReadAllText(arquivo).Should().Be("verde\nverde\nverde");
        }

        [Fact]
        public void OTrechoQueNAO_Existe_MandaLerOArquivo()
        {
            string arquivo = Criar("a.txt", "conteúdo");

            string? recusa = new EditFileTool().Validar(JsonSerializer.Serialize(new
            {
                path = arquivo,
                old_string = "não está lá",
                new_string = "x"
            }));

            recusa.Should().Contain("não existe em");
            recusa.Should().Contain("read", "a ferramenta certa é nomeada");
        }

        [Fact]
        public void ArquivoInexistente_RESPONDE_ComOQueExiste()
        {
            Criar("users.csv", "id;nome");

            string? recusa = new EditFileTool().Validar(JsonSerializer.Serialize(new
            {
                path = Path.Combine(_dir, "users.xls"),
                old_string = "a",
                new_string = "b"
            }));

            recusa.Should().Contain("não existe");
            recusa.Should().Contain("users.csv");
        }

        [Fact]
        public void OEdit_PEDE_Confirmacao_EMostraOAntesEODepois()
        {
            // Autorizar uma edição sem ver o que muda seria autorizar no escuro.
            string arquivo = Criar("a.txt", "velho");
            var tool = new EditFileTool();

            tool.RequiresConfirmation.Should().BeTrue();

            var ctx = tool.BuildConfirmationContext(JsonSerializer.Serialize(new
            {
                path = arquivo,
                old_string = "velho",
                new_string = "novo"
            }), 9);

            ctx.Should().NotBeNull();
            ctx!.Command.Should().Contain("EDITAR").And.Contain(arquivo);
            ctx.ScriptBody.Should().Contain("- velho").And.Contain("+ novo");
        }

        [Theory]
        [InlineData("aaa", "a", 3)]
        [InlineData("abcabc", "abc", 2)]
        [InlineData("nada", "z", 0)]
        [InlineData("qualquer", "", 0)]
        public void AContagem_EhExata(string texto, string trecho, int esperado)
        {
            EditFileTool.Contar(texto, trecho).Should().Be(esperado);
        }

        [Fact]
        public void ASubstituicao_TROCA_SoAPrimeira()
        {
            // string.Replace troca todas. A diferença é a razão de o método existir.
            EditFileTool.Substituir("a-a-a", "a", "X").Should().Be("X-a-a");
        }

        // ─────────────────────────────────────────────────────────────────────
        // glob
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void OGlob_ACHA_PeloPadrao()
        {
            Criar("a.html", "x");
            Criar("b.html", "x");
            Criar("c.txt", "x");

            string saida = GlobTool.Procurar(_dir, "*.html");

            saida.Should().Contain("a.html").And.Contain("b.html");
            saida.Should().NotContain("c.txt");
        }

        [Fact]
        public void SemDoisAsteriscos_NaoENTRA_NasSubpastas()
        {
            Criar("raiz.cs", "x");
            Criar(Path.Combine("dentro", "fundo.cs"), "x");

            GlobTool.Procurar(_dir, "*.cs").Should().NotContain("fundo.cs");
            GlobTool.Procurar(_dir, "**/*.cs").Should().Contain("fundo.cs");
        }

        [Fact]
        public void SemAchadoNENHUM_ElaDizOQueExiste()
        {
            // Nada achado não pode ser um beco: dizer o que EXISTE é o que impede a próxima
            // tentativa de ser outro chute. Foi a lição das quatro chamadas idênticas.
            Criar("users.csv", "x");

            string saida = GlobTool.Procurar(_dir, "*.xlsx");

            saida.Should().Contain("Nenhum arquivo casa");
            saida.Should().Contain("users.csv");
            saida.Should().Contain("**/", "e ensina como procurar mais fundo");
        }

        [Fact]
        public async Task OGlob_NaoPede_Confirmacao()
        {
            // Saber que um arquivo existe não muda a máquina de ninguém.
            // Membro padrao da interface: so existe pelo ITool, e nao pela classe concreta.
            var tool = new GlobTool();
            ((ITool)tool).RequiresConfirmation.Should().BeFalse();

            Criar("x.txt", "x");
            string saida = await tool.ExecuteAsync(
                JsonSerializer.Serialize(new { pattern = "*.txt", path = _dir }));

            saida.Should().Contain("x.txt");
        }

        // ─────────────────────────────────────────────────────────────────────
        // grep
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void OGrep_DEVOLVE_ArquivoLinhaETrecho()
        {
            Criar("codigo.cs", "using System;\nvoid Montar()\n{\n    // nada\n}");

            string saida = GrepTool.Buscar(_dir, "Montar");

            saida.Should().Contain("codigo.cs:2:");
            saida.Should().Contain("void Montar()");
            saida.Should().Contain("1 acerto(s)");
        }

        [Fact]
        public void OGrep_TRAZ_SoALinha_NaoOArquivo()
        {
            // É a diferença entre gastar vinte tokens e gastar doze mil.
            Criar("grande.txt", string.Join("\n", Enumerable.Range(1, 500).Select(i => $"linha {i}"))
                                + "\nAGULHA");

            string saida = GrepTool.Buscar(_dir, "AGULHA");

            saida.Should().Contain("AGULHA");
            saida.Should().NotContain("linha 250");
        }

        [Fact]
        public void OGlobDoGrep_FILTRA_OsArquivos()
        {
            Criar("a.cs", "alvo");
            Criar("b.txt", "alvo");

            string saida = GrepTool.Buscar(_dir, "alvo", "*.cs");

            saida.Should().Contain("a.cs");
            saida.Should().NotContain("b.txt");
        }

        [Fact]
        public void RegexINVALIDA_ExplicaEmVezDeEstourar()
        {
            // Regex escrita de cabeça sai errada com frequência. Dizer QUAL é o problema é o que
            // evita a segunda tentativa às cegas.
            string saida = GrepTool.Buscar(_dir, "[nao-fecha");

            saida.Should().StartWith("ERRO");
            saida.Should().Contain("inválida");
            saida.Should().Contain("escape");
        }

        [Fact]
        public void SemAcertoNenhum_DizQuantosArquivosOlhou()
        {
            Criar("a.txt", "nada aqui");

            string saida = GrepTool.Buscar(_dir, "inexistente");

            saida.Should().Contain("Nada casa");
            saida.Should().Contain("arquivo(s) examinado(s)");
        }

        [Fact]
        public void IgnorarCaixa_EhOpcional()
        {
            Criar("a.txt", "Contrato");

            GrepTool.Buscar(_dir, "contrato").Should().Contain("Nada casa");
            GrepTool.Buscar(_dir, "contrato", "*", ignorarCaixa: true).Should().Contain("a.txt");
        }
    }
}
