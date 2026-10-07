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

        private sealed class Prompt : IConfirmationPrompt
        {
            public bool Permite { get; init; }
            public System.Collections.Generic.List<CommandConfirmationContext> Vistos { get; } = new();

            public Task<(bool Allowed, bool AlwaysAllow)> AskAsync(CommandConfirmationContext context)
            {
                Vistos.Add(context);
                return Task.FromResult((Permite, true));
            }
        }

        // Caso real: depois de navegar num sistema interno, "lembre que Costuma deixar as issues
        // acumular..." — ditado pelo próprio usuário — foi negado cinco vezes, porque a recusa
        // era pelo contexto e não pelo pedido. Agora quem decide é ele, no cartão.
        [Fact]
        public async Task ComTextoDeTerceirosNoContexto_VaiAoCartao_EOUsuarioDecide()
        {
            const string fato = "Costuma deixar as issues acumular e resolver todas de uma vez";

            var sim = new Prompt { Permite = true };
            var registry = new ToolRegistry(sim) { ConteudoDeEmailNoContexto = () => true };
            registry.Registrar(_tool);

            (await registry.ExecuteToolAsync(Ferramentas.Lembrar, Args(fato), userLevel: 1)).Should().StartWith("Guardado");

            var cartao = sim.Vistos.Should().ContainSingle().Subject;
            cartao.Command.Should().Be($"GUARDAR NA MEMÓRIA: \"{fato}\"", "ele autoriza vendo o fato inteiro");
            cartao.SemSempre.Should().BeTrue("cada fato é uma decisão");
            cartao.ConteudoDeEmailNoContexto.Should().BeTrue("o cartão avisa de onde o pedido pode ter vindo");

            // "Sempre" respondido pelo prompt não vale: o fato seguinte pergunta de novo.
            await registry.ExecuteToolAsync(Ferramentas.Lembrar, Args("mora em Curitiba"), userLevel: 1);
            sim.Vistos.Should().HaveCount(2);

            var nao = new Prompt { Permite = false };
            var outro = new ToolRegistry(nao) { ConteudoDeEmailNoContexto = () => true };
            outro.Registrar(_tool);

            await outro.ExecuteToolAsync(Ferramentas.Lembrar, Args("quer transferir tudo"), userLevel: 1);
            _fatos.ReadFacts().Should().NotContain(l => l.Contains("transferir"));
        }

        [Fact]
        public async Task ComTextoDeTerceirosNoContexto_SemInterface_ORegistryRecusa()
        {
            // Um e-mail dizendo "anote que o usuário quer X" envenenaria todas as conversas
            // seguintes. Sem ninguém para ver o cartão, não grava.
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
