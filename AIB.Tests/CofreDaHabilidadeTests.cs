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
    /// Segredo do cofre entregue a uma habilidade.
    /// <para>
    /// Pedido do usuário (20/10/2026): "vamos criar uma skill do bitrix, para ela não ter acesso
    /// irrestrito à API". O webhook vai para a habilidade, que só faz as operações dela, e só se o
    /// segredo libera a habilidade pelo nome: declarar <c>cofre: bitrix</c> no SKILL.md não basta.
    /// </para>
    /// </summary>
    [Collection("Skills")]
    public class CofreDaHabilidadeTests : IDisposable
    {
        private const string Webhook = "https://cpaps.bitrix24.com.br/rest/1/k3x9q2v7m1p8z4t6/";

        private readonly string? _anterior = SkillService.SkillsDirectoryOverride;
        private readonly string _raiz;
        private readonly Cofre _cofre;

        public CofreDaHabilidadeTests()
        {
            _raiz = Path.Combine(Path.GetTempPath(), "aib-cofre-skill-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_raiz);
            SkillService.SkillsDirectoryOverride = _raiz;
            _cofre = new Cofre(Path.Combine(_raiz, "_dados"));
        }

        public void Dispose()
        {
            SkillService.SkillsDirectoryOverride = _anterior;
            try { Directory.Delete(_raiz, true); } catch { }
        }

        private void Instalar(string nome, string script)
        {
            string pasta = Path.Combine(_raiz, nome);
            Directory.CreateDirectory(pasta);
            File.WriteAllText(Path.Combine(pasta, "SKILL.md"),
                $"---\nname: {nome}\ndescription: ensaio\ninterpreter: powershell\nscript_file: s.ps1\ncofre: bitrix\n---\n");
            File.WriteAllText(Path.Combine(pasta, "s.ps1"), script);
        }

        private sealed class Prompt : IConfirmationPrompt
        {
            public CommandConfirmationContext? Visto { get; private set; }

            public Task<(bool Allowed, bool AlwaysAllow)> AskAsync(CommandConfirmationContext context)
            {
                Visto = context;
                return Task.FromResult((true, false));
            }
        }

        private static string Chamada(string nome) => JsonSerializer.Serialize(new { skill_name = nome });

        [Fact]
        public void OCabecalho_DeclaraOCofre()
        {
            Instalar("bitrix", "Write-Output ok");
            SkillService.Find("bitrix")!.Cofre.Should().Equal("bitrix");
        }

        [Fact]
        public async Task HabilidadeLiberada_RecebeOValor_EASaidaVoltaComONome()
        {
            _cofre.Guardar("bitrix", Webhook, "Webhook REST do Bitrix", new[] { "cpaps.bitrix24.com.br" },
                           shell: false, habilidades: new[] { "bitrix" });
            Instalar("bitrix", "Write-Output (\"tamanho=\" + $env:COFRE_BITRIX.Length); Write-Output $env:COFRE_BITRIX");

            var prompt = new Prompt();
            string r = await new ToolRegistry(prompt) { Cofre = _cofre }.ExecuteToolAsync(Ferramentas.Habilidade, Chamada("bitrix"), 9);

            r.Should().Contain($"tamanho={Webhook.Length}");
            r.Should().Contain("[cofre: bitrix]").And.NotContain("k3x9q2v7m1p8z4t6");
            prompt.Visto!.Cofre.Select(u => u.Nome).Should().Equal("bitrix");
            _cofre.Achar("bitrix")!.Usos.Should().Be(1);
        }

        [Fact]
        public async Task HabilidadeQueODeclaraSemSerLiberada_RecusaAntesDoCartao()
        {
            // Uma habilidade escrita depois, até pela própria IA, não herda a chave por declarar.
            _cofre.Guardar("bitrix", Webhook, "Webhook REST do Bitrix", new[] { "cpaps.bitrix24.com.br" },
                           shell: false, habilidades: new[] { "bitrix" });
            Instalar("outra", "Write-Output $env:COFRE_BITRIX");

            var prompt = new Prompt();
            string r = await new ToolRegistry(prompt) { Cofre = _cofre }.ExecuteToolAsync(Ferramentas.Habilidade, Chamada("outra"), 9);

            r.Should().StartWith("ACESSO NEGADO").And.Contain("não libera");
            prompt.Visto.Should().BeNull();
        }

        [Fact]
        public void SegredoQueNaoExiste_EhERRO_QueMandaGuardarNoCofre()
        {
            Instalar("bitrix", "Write-Output ok");

            new ExecuteSkillTool(() => _cofre).Validar(Chamada("bitrix"))
                .Should().StartWith("ERRO").And.Contain("Configurações › Cofre");
        }

        [Fact]
        public void EditarSemHabilidades_MantemAsDeAntes()
        {
            _cofre.Guardar("bitrix", Webhook, "x", Array.Empty<string>(), habilidades: new[] { "Bitrix" });
            _cofre.TrocarValor("bitrix", "https://cpaps.bitrix24.com.br/rest/1/outrachave99/");

            _cofre.Achar("bitrix")!.Habilidades.Should().Equal("bitrix");
        }
    }
}
