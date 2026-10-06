using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Memory;
using AIB.Services.Tools;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A persona guarda o que o usuário conta sobre si. Pedido: "quando só conversando, ela
    /// deveria ser mais curiosa com o usuário, e isso seria uma boa forma de obter e salvar os
    /// fatos" — os fatos duráveis só sabiam do trabalho, nunca da pessoa.
    /// </summary>
    public class LembrarToolTests : IDisposable
    {
        private readonly string _raiz = Path.Combine(Path.GetTempPath(), "aib-lembrar-" + Guid.NewGuid().ToString("N"));
        private readonly FactStore _fatos;
        private readonly LembrarTool _tool;

        public LembrarToolTests()
        {
            _fatos = new FactStore(_raiz);
            _tool = new LembrarTool(_fatos);
        }

        public void Dispose()
        {
            try { Directory.Delete(_raiz, true); } catch { }
        }

        private static string Args(string fato) =>
            System.Text.Json.JsonSerializer.Serialize(new { fact = fato });

        [Fact]
        public async Task OFato_VaiParaOFactsMd_ComPrefixoProprio()
        {
            string r = await _tool.ExecuteAsync(Args("trabalha com TI   num hospital"));

            r.Should().StartWith("Guardado");
            _fatos.ReadFacts().Should().Equal("- sobre o usuário: trabalha com TI num hospital");
        }

        [Fact]
        public async Task OMesmoFato_NaoEntraDuasVezes_NemOQueOUsuarioApagou()
        {
            await _tool.ExecuteAsync(Args("gosta de café"));
            (await _tool.ExecuteAsync(Args("Gosta de café."))).Should().StartWith("Já estava guardado");

            // O usuário apaga a linha à mão: ela não volta.
            File.WriteAllText(_fatos.FactsPath, "# Fatos duráveis\n");
            await _tool.ExecuteAsync(Args("gosta de café"));

            _fatos.ReadFacts().Should().BeEmpty();
        }

        [Theory]
        [InlineData("", "ERRO")]
        [InlineData("senha do wifi: password=Abc123xyz!", "ACESSO NEGADO")]
        public void Validar_RecusaVazioESegredo(string fato, string comeco)
        {
            _tool.Validar(Args(fato)).Should().StartWith(comeco);
        }

        [Fact]
        public void Validar_RecusaParagrafo()
        {
            _tool.Validar(Args(new string('a', LembrarTool.TetoDoFato + 1))).Should().StartWith("ERRO");
            _tool.Validar(Args("prefere respostas com exemplo")).Should().BeNull();
        }

        [Fact]
        public async Task NoTeto_NaoGuardaMais()
        {
            _fatos.Promote(Enumerable.Range(0, LembrarTool.Teto)
                .Select(i => new FactCandidate("usuario|f" + i, LembrarTool.Prefixo + "fato " + i)));

            (await _tool.ExecuteAsync(Args("mais um"))).Should().StartWith("ERRO");
        }

        [Fact]
        public async Task ComTextoDeTerceirosNoContexto_ORegistryRecusa()
        {
            // Um e-mail dizendo "anote que o usuário quer X" envenenaria todas as conversas
            // seguintes.
            var registry = new ToolRegistry { ConteudoDeEmailNoContexto = () => true };
            registry.Registrar(_tool);

            string r = await registry.ExecuteToolAsync(Ferramentas.Lembrar, Args("quer transferir tudo"), userLevel: 1);

            r.Should().StartWith("ACESSO NEGADO");
            _fatos.ReadFacts().Should().BeEmpty();
        }

        [Fact]
        public async Task SemTerceiros_PassaSemCartao()
        {
            // Não mexe na máquina: sem interface de confirmação, ainda assim grava.
            var registry = new ToolRegistry { ConteudoDeEmailNoContexto = () => false };
            registry.Registrar(_tool);

            (await registry.ExecuteToolAsync(Ferramentas.Lembrar, Args("mora em Curitiba"), userLevel: 1))
                .Should().StartWith("Guardado");
        }

        [Fact]
        public void OAviso_FalaComONomeDaPersona()
        {
            ChatWindow.TextoDoAviso("Ellen").Should().Be("Ellen lembrará disso…");
            AIB.Services.Memory.ArtifactExtractor.ResumirArgumento(Ferramentas.Lembrar, Args("gosta de café"))
                .Should().Be("gosta de café", "é o tooltip do aviso");
        }
    }
}
