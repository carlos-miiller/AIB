using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AIB.Services;
using AIB.Services.Tools;
using AIB.Views;
using FluentAssertions;
using Xunit;

using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;

namespace AIB.Tests
{
    /// <summary>
    /// Comandos de shell e chamadas <c>file</c> da mesma resposta num cartão só, com Permitir e
    /// Recusar por item, como as gravações da doc. Pedido do usuário (18/11/2026): "execução
    /// simultânea e, quando inserido um parâmetro, execução sequencial, como fizemos na ferramenta
    /// doc" — até aqui, cada chamada de um lote paralelo abria o seu cartão. A ordem continua com o
    /// <c>ops</c> da file (e com <c>;</c>/<c>&amp;&amp;</c> num comando só).
    /// </summary>
    [Collection("Escrita")]
    public class CartaoDeVariasChamadasTests : IDisposable
    {
        private readonly string _dir;

        public CartaoDeVariasChamadasTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "aib-cartao-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            PastasSemConfirmacao.Configurar("");
            AlwaysAllowSession.Clear();
        }

        public void Dispose()
        {
            PastasSemConfirmacao.Configurar("");
            AlwaysAllowSession.Clear();
            try { Directory.Delete(_dir, true); } catch { }
        }

        /// <summary>Permite o cartão, mas recusa os itens nas posições dadas, como o botão de cada um.</summary>
        private sealed class PromptQueRecusaAlguns : IConfirmationPrompt
        {
            private readonly int[] _recusar;
            public List<CommandConfirmationContext> Perguntas { get; } = new();

            public PromptQueRecusaAlguns(params int[] recusar) => _recusar = recusar;

            public Task<(bool Allowed, bool AlwaysAllow)> AskAsync(CommandConfirmationContext context)
            {
                lock (Perguntas) Perguntas.Add(context);
                foreach (int i in _recusar) context.Recusados.Add(i);
                return Task.FromResult((true, false));
            }
        }

        private static async Task<string[]> EmParalelo(ToolRegistry registry, IReadOnlyList<(string Nome, string Args)> chamadas)
        {
            var vagas = registry.Agrupar(chamadas);
            vagas.Should().OnlyContain(v => v != null, "as chamadas da mesma resposta dividem o cartão");
            return await Task.WhenAll(chamadas.Select((c, i) => Task.Run(() =>
                registry.ExecuteToolAsync(c.Nome, c.Args, 9, vaga: vagas[i]))))
                .WaitAsync(TimeSpan.FromSeconds(60));
        }

        [Fact]
        public async Task TresComandosNaMesmaResposta_UmCartao_EORecusadoSoNaoRoda()
        {
            var prompt = new PromptQueRecusaAlguns(1);
            var registry = new ToolRegistry(prompt);

            string Marca(string nome) => JsonSerializer.Serialize(new
            {
                command = $"Set-Content -LiteralPath '{Path.Combine(_dir, nome)}' -Value 1"
            });
            var chamadas = new[] { "a.txt", "b.txt", "c.txt" }.Select(n => (Ferramentas.Shell, Marca(n))).ToList();

            var r = await EmParalelo(registry, chamadas);

            prompt.Perguntas.Should().ContainSingle().Which.Itens.Should().HaveCount(3);
            r[1].Should().Be(ToolRegistry.RecusaDoUsuario);
            File.Exists(Path.Combine(_dir, "a.txt")).Should().BeTrue();
            File.Exists(Path.Combine(_dir, "b.txt")).Should().BeFalse("o comando recusado no botão dele não roda");
            File.Exists(Path.Combine(_dir, "c.txt")).Should().BeTrue();
        }

        [Fact]
        public async Task ChamadasFileNaMesmaResposta_UmCartao_OLoteEntraComoUmItem()
        {
            var prompt = new PromptQueRecusaAlguns(0);
            var registry = new ToolRegistry(prompt);

            string a = Path.Combine(_dir, "a.txt"), b = Path.Combine(_dir, "b.txt"), pasta = Path.Combine(_dir, "lote");
            var chamadas = new List<(string, string)>
            {
                (Ferramentas.Arquivo, JsonSerializer.Serialize(new { action = "write", path = a, content = "um" })),
                (Ferramentas.Arquivo, JsonSerializer.Serialize(new { action = "write", path = b, content = "dois" })),
                (Ferramentas.Arquivo, JsonSerializer.Serialize(new
                {
                    ops = new object[]
                    {
                        new { action = "mkdir", path = pasta },
                        new { action = "write", path = Path.Combine(pasta, "c.txt"), content = "três" }
                    }
                }))
            };

            var r = await EmParalelo(registry, chamadas);

            prompt.Perguntas.Should().ContainSingle().Which.Itens.Should().HaveCount(3, "o lote do ops é um item só");
            r[0].Should().Be(ToolRegistry.RecusaDoUsuario);
            File.Exists(a).Should().BeFalse();
            File.ReadAllText(b).Should().Be("dois");
            File.ReadAllText(Path.Combine(pasta, "c.txt")).Should().Be("três", "dentro do lote a ordem vale: a pasta nasce antes");
        }

        [Fact]
        public async Task ComandoJaLiberadoPeloSempre_NaoSeguraOCartaoDosOutros()
        {
            // O "sempre" sai da vaga antes do cartão: se não saísse, o grupo esperaria para sempre.
            string json = JsonSerializer.Serialize(new { command = "Write-Output um" });
            AlwaysAllowSession.Add((Ferramentas.Shell, "Write-Output um", null));
            var prompt = new PromptQueRecusaAlguns();
            var registry = new ToolRegistry(prompt);

            var r = await EmParalelo(registry, new[]
            {
                (Ferramentas.Shell, json),
                (Ferramentas.Shell, JsonSerializer.Serialize(new { command = "Write-Output dois" })),
                (Ferramentas.Shell, JsonSerializer.Serialize(new { command = "Write-Output tres" }))
            });

            prompt.Perguntas.Should().ContainSingle().Which.Itens.Should().HaveCount(2);
            r[0].Should().Contain("um");
            r[2].Should().Contain("tres");
        }

        [Fact]
        public void NoCartao_ComandosTemTituloProprio_AvisosDeTodos_ESemSempre()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var itens = new[]
                {
                    new CommandConfirmationContext { Tool = Ferramentas.Shell, Command = "git status" },
                    new CommandConfirmationContext
                    {
                        Tool = Ferramentas.Shell, Command = "irm $env:COFRE_BITRIX/profile",
                        Cofre = new[] { new UsoDoCofre("BITRIX", "webhook do Bitrix", new[] { "empresa.bitrix24.com.br" }) }
                    }
                };
                var juntado = new RunCommandTool().JuntarNoCartao(itens);

                var card = new ConfirmCardView();
                card.Preencher(juntado);

                ((TextBlock)card.FindName("TituloText")).Text.Should().Be("Rodar 2 comandos?");
                ((TextBlock)card.FindName("ConsequenciaText")).Text.Should().Contain("AO MESMO TEMPO");
                ((Panel)card.FindName("ItensPanel")).Children.Count.Should().Be(2);
                ((Button)card.FindName("PermitirButton")).Content.Should().Be("Permitir os 2");
                ((FrameworkElement)card.FindName("CofreBorder")).Visibility.Should().Be(Visibility.Visible,
                    "o alias do cofre de um dos comandos aparece no cartão juntado");
                ((CheckBox)card.FindName("SempreCheck")).Visibility.Should().Be(Visibility.Collapsed,
                    "um \"sempre\" valeria para comandos recusados um a um");
            });
        }

        [Fact]
        public void NoCartao_ChamadasFile_TituloProprio_ELoteMostraAsOperacoes()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var itens = new[]
                {
                    new CommandConfirmationContext { Tool = Ferramentas.Gravar, Command = @"CRIAR C:\t\a.txt", ScriptBody = "um" },
                    new CommandConfirmationContext { Tool = Ferramentas.Arquivo, Command = "1. CRIAR PASTA C:\\t\\x\n2. CRIAR C:\\t\\x\\b.txt" }
                };
                var juntado = new FileTool().JuntarNoCartao(itens);

                var card = new ConfirmCardView();
                card.Preencher(juntado);

                ((TextBlock)card.FindName("TituloText")).Text.Should().Be("Fazer 2 mudanças em arquivos?");
                var textos = Textos((Panel)card.FindName("ItensPanel")).ToList();
                textos.Should().Contain("CRIAR").And.Contain(@"C:\t\a.txt");
                textos.Should().Contain("2 operações, em ordem");
            });
        }

        private static IEnumerable<string> Textos(DependencyObject raiz)
        {
            if (raiz is TextBlock t) yield return t.Text;
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(raiz);
            if (n == 0 && raiz is FrameworkElement fe)
                foreach (var filho in LogicalTreeHelper.GetChildren(fe).OfType<DependencyObject>())
                    foreach (var s in Textos(filho)) yield return s;
            for (int i = 0; i < n; i++)
                foreach (var s in Textos(System.Windows.Media.VisualTreeHelper.GetChild(raiz, i))) yield return s;
        }
    }
}
