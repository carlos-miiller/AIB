using System;
using System.IO;
using System.Text.Json;
using AIB.Services;
using AIB.Services.Tools;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O confinamento de pasta das ferramentas de escrita.
    /// <para>
    /// O <c>WriteFileTool</c> carregava o buraco escrito num comentário: "sem confinamento de
    /// raiz, o caminho resolvido é a única defesa que o usuário tem". Mostrar o caminho no modal
    /// não impede nada — quem clica "permitir" às pressas autoriza uma gravação em
    /// <c>...\Startup\</c> do mesmo jeito.
    /// </para>
    /// </summary>
    [Collection("Escrita")]
    public class PastasPermitidasTests : IDisposable
    {
        private readonly string _dir;

        public PastasPermitidasTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "aib-raiz-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            // O estado estático não pode vazar para outra classe de teste: o xUnit roda as
            // classes em paralelo, e um confinamento esquecido aqui reprovaria testes de escrita
            // em outro arquivo. Foi exatamente o que o SkillsDirectoryOverride já custou uma vez.
            PastasPermitidas.Configurar("");
            try { Directory.Delete(_dir, true); } catch { }
        }

        // ─────────────────────────────────────────────────────────────────────
        // A lista
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ListaVAZIA_NaoConfinaNada()
        {
            // É o comportamento de sempre. Ligar o confinamento sozinho quebraria quem usa a AIB
            // para mexer em projeto fora da pasta pessoal.
            PastasPermitidas.Barrar(@"C:\qualquer\lugar\a.txt", "").Should().BeNull();
            PastasPermitidas.Barrar(@"C:\qualquer\lugar\a.txt", null).Should().BeNull();
            PastasPermitidas.Barrar(@"C:\qualquer\lugar\a.txt", "   \n  \n ").Should().BeNull();
        }

        [Fact]
        public void AsLinhas_ViramCaminhos_SemRepeticaoESemLixo()
        {
            var raizes = PastasPermitidas.Analisar(
                "  C:\\um  \n\n\"C:\\dois\"\r\nC:\\um\\\nC:\\UM\n");

            raizes.Should().HaveCount(2, "aspas, espaço, barra final e caixa não fazem pasta nova");
            raizes[0].Should().Be(@"C:\um");
            raizes[1].Should().Be(@"C:\dois");
        }

        // ─────────────────────────────────────────────────────────────────────
        // O cerco
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void DentroDaRaiz_Passa()
        {
            string dentro = Path.Combine(_dir, "sub", "a.txt");

            PastasPermitidas.Barrar(dentro, _dir).Should().BeNull();
            PastasPermitidas.Barrar(_dir, _dir).Should().BeNull("a própria raiz vale");
        }

        [Fact]
        public void ForaDaRaiz_EhBarrado_EARecusaDizOndePODE()
        {
            string fora = Path.Combine(Path.GetTempPath(), "outra-arvore", "a.txt");

            string? recusa = PastasPermitidas.Barrar(fora, _dir);

            recusa.Should().NotBeNull();
            recusa.Should().Contain(_dir, "um 'não pode' sem endereço faz o modelo chutar o caminho seguinte");
        }

        [Fact]
        public void PontoPonto_NaoEscapa()
        {
            // O caminho começa dentro da raiz e sai dela pelo texto. Path.GetFullPath resolve o
            // '..' antes da comparação — sem isso o prefixo casaria e a gravação cairia fora.
            string fuga = Path.Combine(_dir, "..", "vizinho.txt");

            PastasPermitidas.Barrar(fuga, _dir).Should().NotBeNull();
        }

        [Fact]
        public void PastaVIZINHA_ComOMesmoComeco_NaoEntra()
        {
            // "C:\dados" e "C:\dados-2": a comparação por prefixo cru deixaria a segunda passar.
            string raiz = Path.Combine(_dir, "dados");
            string vizinha = Path.Combine(_dir, "dados-2", "a.txt");

            PastasPermitidas.Barrar(vizinha, raiz).Should().NotBeNull();
            PastasPermitidas.Barrar(Path.Combine(raiz, "a.txt"), raiz).Should().BeNull();
        }

        [Fact]
        public void VariasRaizes_BastaUmaCasar()
        {
            string outra = Path.Combine(Path.GetTempPath(), "aib-outra-" + Guid.NewGuid().ToString("N"));
            string lista = _dir + Environment.NewLine + outra;

            PastasPermitidas.Barrar(Path.Combine(outra, "a.txt"), lista).Should().BeNull();
            PastasPermitidas.Barrar(Path.Combine(_dir, "a.txt"), lista).Should().BeNull();
            PastasPermitidas.Barrar(@"C:\terceiro\a.txt", lista).Should().NotBeNull();
        }

        // ─────────────────────────────────────────────────────────────────────
        // As ferramentas
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void OWrite_RECUSA_AntesDoModal()
        {
            // Recusar em Validar é o que faz a pergunta NEM EXISTIR. Se o caminho chegasse ao
            // modal, bastaria um clique distraído para a defesa não valer nada.
            PastasPermitidas.Configurar(Path.Combine(_dir, "permitida"));

            string args = JsonSerializer.Serialize(new
            {
                path = Path.Combine(_dir, "proibida", "a.txt"),
                content = "x"
            });

            string? recusa = new WriteFileTool().Validar(args);

            recusa.Should().NotBeNull();
            recusa.Should().Contain("fora das pastas");
        }

        [Fact]
        public void OEdit_RECUSA_MesmoComOArquivoExistindo()
        {
            string alvo = Path.Combine(_dir, "a.txt");
            File.WriteAllText(alvo, "velho");

            PastasPermitidas.Configurar(Path.Combine(_dir, "so-aqui"));

            string? recusa = new EditFileTool().Validar(JsonSerializer.Serialize(new
            {
                path = alvo,
                old_string = "velho",
                new_string = "novo"
            }));

            recusa.Should().NotBeNull();
            recusa.Should().Contain("fora das pastas");
            File.ReadAllText(alvo).Should().Be("velho");
        }

        [Fact]
        public void SemConfinamento_AsDuasSeguemComoAntes()
        {
            PastasPermitidas.Configurar("");

            string alvo = Path.Combine(_dir, "a.txt");
            File.WriteAllText(alvo, "velho");

            new WriteFileTool().Validar(JsonSerializer.Serialize(new { path = alvo, content = "x" }))
                .Should().BeNull();

            new EditFileTool().Validar(JsonSerializer.Serialize(new
            {
                path = alvo,
                old_string = "velho",
                new_string = "novo"
            })).Should().BeNull();
        }

        // ─────────────────────────────────────────────────────────────────────
        // A lista de "sempre permitir"
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void AsAutorizacoesDaSessao_PodemSerVistas_EEsquecidas()
        {
            // Uma allowlist que o usuário não consegue VER é uma decisão de segurança tomada por
            // ele e depois escondida dele.
            AlwaysAllowSession.Clear();
            AlwaysAllowSession.Add(("shell", "git status", null));
            AlwaysAllowSession.Add(("skill", "montar-planilha", "abc123"));

            AlwaysAllowSession.Quantos.Should().Be(2);
            AlwaysAllowSession.Listar().Should().Contain(x => x.Cmd == "git status");

            AlwaysAllowSession.Clear();
            AlwaysAllowSession.Quantos.Should().Be(0);
            AlwaysAllowSession.Listar().Should().BeEmpty();
        }
    }
}
