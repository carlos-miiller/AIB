using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AIB.Services;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// As pastas do AIB (dados e programa) são só leitura para as ferramentas. Pedido do usuário
    /// (11/11/2026): "uma trava em cima das ferramentas de ellen para ela não conseguir mudar nada em
    /// sua pasta". A Ellen já tinha reescrito o script da skill do Bitrix, que é o que impede a skill
    /// de ler as conversas particulares do usuário.
    /// </summary>
    [Collection("Escrita")]
    public class PastasDoAibTests : IDisposable
    {
        private readonly string _raiz;
        private readonly string _dados;
        private readonly string _programa;
        private readonly string _fora;

        public PastasDoAibTests()
        {
            _raiz = Path.Combine(Path.GetTempPath(), "aib-pastas-" + Guid.NewGuid().ToString("N"));
            _dados = Path.Combine(_raiz, ".AIB");
            _programa = Path.Combine(_raiz, "programa");
            _fora = Path.Combine(_raiz, "fora");
            Directory.CreateDirectory(Path.Combine(_dados, "skills", "bitrix"));
            Directory.CreateDirectory(_programa);
            Directory.CreateDirectory(_fora);
        }

        public void Dispose()
        {
            try { Directory.Delete(_raiz, true); } catch { }
        }

        private string? Recusa(string ferramenta, object args) =>
            PastasDoAib.Recusa(ferramenta, JsonSerializer.Serialize(args), _dados, _programa);

        private string Script => Path.Combine(_dados, "skills", "bitrix", "bitrix.ps1");

        [Fact]
        public void GravarEEditar_NasPastasDoAib_SaoRecusados_EForaPassa()
        {
            Recusa("file", new { action = "write", path = Script, content = "x" })
                .Should().StartWith("ACESSO NEGADO").And.Contain("pasta de dados do AIB");
            Recusa("file", new { action = "edit", path = Script, old_string = "a", new_string = "b" })
                .Should().StartWith("ACESSO NEGADO");
            Recusa("file", new { action = "write", path = Path.Combine(_programa, "AIB.dll"), content = "x" })
                .Should().Contain("pasta do programa AIB");
            Recusa("write", new { path = Script, content = "x" }).Should().NotBeNull("a ferramenta antiga também");

            Recusa("file", new { action = "write", path = Path.Combine(_fora, "a.txt"), content = "x" }).Should().BeNull();
        }

        [Fact]
        public void LoteComUmaOperacaoNaPasta_RecusaOLoteInteiro()
        {
            var args = new
            {
                ops = new object[]
                {
                    new { action = "write", path = Path.Combine(_fora, "a.txt"), content = "x" },
                    new { action = "rename", path = Script, new_name = "velho.ps1" }
                }
            };

            Recusa("file", args).Should().StartWith("ACESSO NEGADO");
        }

        [Fact]
        public void CopiarDaPasta_SoLe_MasMoverOuCopiarParaDentroNao()
        {
            // O caminho que a trava fecha: mover o script para outro nome e gravar um novo no lugar.
            Recusa("file", new { action = "copy", path = Script, destination = Path.Combine(_fora, "b.ps1") }).Should().BeNull();
            Recusa("file", new { action = "move", path = Script, destination = Path.Combine(_fora, "b.ps1") }).Should().NotBeNull();
            Recusa("file", new { action = "copy", path = Path.Combine(_fora, "b.ps1"), destination = Script }).Should().NotBeNull();
            Recusa("file", new { action = "delete", path = Path.Combine(_dados, "skills") }).Should().NotBeNull();
            Recusa("file", new { action = "mkdir", path = Path.Combine(_dados, "skills", "nova") }).Should().NotBeNull();
        }

        [Fact]
        public void Doc_LerPassa_GravarNaPastaNao()
        {
            string planilha = Path.Combine(_dados, "modelos", "base.xlsx");
            Recusa("doc", new { action = "read", path = planilha }).Should().BeNull();
            Recusa("doc", new { action = "edit", path = planilha, ops = new object[] { new { cell = "A1", value = "x" } } })
                .Should().NotBeNull();
            Recusa("doc", new { action = "create", path = Path.Combine(_fora, "nova.xlsx"), from = planilha }).Should().BeNull();
        }

        [Fact]
        public void JuncaoForaApontandoParaDentro_ERecusada()
        {
            // Uma pasta comum fora, que é junção para ~/.AIB: gravaria lá pelo outro nome.
            string juncao = Path.Combine(_fora, "atalho");
            var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{juncao}\" \"{_dados}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            })!;
            p.WaitForExit();
            Directory.Exists(juncao).Should().BeTrue("o ensaio precisa da junção");

            Recusa("file", new { action = "write", path = Path.Combine(juncao, "skills", "bitrix", "bitrix.ps1"), content = "x" })
                .Should().StartWith("ACESSO NEGADO");
        }

        [Fact]
        public async Task NoRegistro_RecusaMesmoComOUsuarioAutorizando_ENadaEGravado()
        {
            // Sem trava, o cartão autorizado gravaria: o prompt de ensaio diz sim a tudo.
            var registry = new ToolRegistry(new PromptQueAutoriza());
            string alvo = Path.Combine(AppContext.BaseDirectory, "aib-trava-" + Guid.NewGuid().ToString("N") + ".txt");
            string? decisao = null;

            string r = await registry.ExecuteToolAsync(
                "file", JsonSerializer.Serialize(new { action = "write", path = alvo, content = "x" }), 9, null, d => decisao = d);

            r.Should().StartWith("ACESSO NEGADO");
            decisao.Should().Be("pasta_do_aib");
            File.Exists(alvo).Should().BeFalse();
        }

        private sealed class PromptQueAutoriza : IConfirmationPrompt
        {
            public Task<(bool Allowed, bool AlwaysAllow)> AskAsync(CommandConfirmationContext context) =>
                Task.FromResult((true, false));
        }
    }
}
