using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AIB.Services;
using AIB.Services.Documentos;
using AIB.Views;
using FluentAssertions;
using Xunit;

using Button = System.Windows.Controls.Button;

namespace AIB.Tests
{
    /// <summary>
    /// Várias gravações de documento num cartão, com Permitir e Recusar por arquivo. Pedido do
    /// usuário (11/11/2026): a Ellen editava a mesma planilha em chamadas seguidas e cada uma abria
    /// o seu cartão — 8 num dia na planilha de comparação. Mockup "Edição de documentos do AIB",
    /// cena "Várias mudanças de uma vez".
    /// </summary>
    [Collection("Escrita")]
    public class VariasGravacoesTests : IDisposable
    {
        private readonly string _dir;

        public VariasGravacoesTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "aib-varias-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            PastasSemConfirmacao.Configurar("");
        }

        public void Dispose()
        {
            PastasSemConfirmacao.Configurar("");
            try { Directory.Delete(_dir, true); } catch { }
        }

        /// <summary>Permite o cartão, mas recusa os arquivos nas posições dadas, como o botão de cada um.</summary>
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

        private string Criar(string arquivo) => JsonSerializer.Serialize(new
        {
            action = "create",
            path = Path.Combine(_dir, arquivo),
            content = new { sheets = new[] { new { name = "Dados", rows = new[] { new object[] { "Nome", "Valor" }, new object[] { "a", 1 } } } } }
        });

        [Fact]
        public async Task TresGravacoesNaMesmaResposta_UmCartao_EORecusadoSoNaoRoda()
        {
            var prompt = new PromptQueRecusaAlguns(1);
            var registry = new ToolRegistry(prompt);

            var chamadas = new[] { "a.xlsx", "b.xlsx", "c.xlsx" }.Select(n => ("doc", Criar(n))).ToList();
            var vagas = registry.Agrupar(chamadas);
            vagas.Should().OnlyContain(v => v != null, "as três gravações dividem o cartão");

            var resultados = await Task.WhenAll(Enumerable.Range(0, 3).Select(i => Task.Run(() =>
                registry.ExecuteToolAsync("doc", chamadas[i].Item2, 9, vaga: vagas[i]))))
                .WaitAsync(TimeSpan.FromSeconds(60));

            prompt.Perguntas.Should().ContainSingle().Which.Itens.Should().HaveCount(3);
            resultados[1].Should().Be(ToolRegistry.RecusaDoUsuario);
            File.Exists(Path.Combine(_dir, "a.xlsx")).Should().BeTrue();
            File.Exists(Path.Combine(_dir, "b.xlsx")).Should().BeFalse("o arquivo recusado no botão dele não é gravado");
            File.Exists(Path.Combine(_dir, "c.xlsx")).Should().BeTrue();
        }

        [Fact]
        public async Task LeituraNaoEntraNoCartaoDasGravacoes()
        {
            var registry = new ToolRegistry(new PromptQueRecusaAlguns());
            var vagas = registry.Agrupar(new[]
            {
                ("doc", Criar("a.xlsx")), ("doc", Criar("b.xlsx")),
                ("doc", JsonSerializer.Serialize(new { action = "read", path = Path.Combine(_dir, "x.xlsx") }))
            });

            vagas[2].Should().BeNull();
            vagas[0]!.Dispose();
            vagas[1]!.Dispose();
            await Task.CompletedTask;
        }

        private static CommandConfirmationContext Item(string arquivo) => new()
        {
            Tool = Ferramentas.Documento,
            Command = "EDITAR " + arquivo,
            Plano = new PlanoDeGravacao
            {
                Arquivo = arquivo,
                Titulo = "Gravar 1 célula nesta planilha?",
                Resumo = "aba \"pdv\" · 1 célula",
                Mudancas = new List<Mudanca> { new(TipoDeMudanca.Valor, "E85", "VEND 12", "VEND 21") }
            },
            SemSempre = true
        };

        [Fact]
        public void NoCartao_CadaArquivoTemOSeuBotao_EOBotaoDeBaixoContaOQueFica()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var itens = new[] { Item(@"C:\t\comparacao.xlsx"), Item(@"C:\t\resumo.xlsx") };
                var juntado = new Services.Tools.DocTool().JuntarNoCartao(itens);

                var card = new ConfirmCardView();
                card.Preencher(juntado);

                ((TextBlock)card.FindName("TituloText")).Text.Should().Be("Gravar em 2 documentos?");
                var painel = (Panel)card.FindName("ItensPanel");
                painel.Visibility.Should().Be(Visibility.Visible);
                painel.Children.Count.Should().Be(2, "um bloco por arquivo");

                var permitir = (Button)card.FindName("PermitirButton");
                permitir.Content.Should().Be("Permitir os 2");

                card.Marcar(0, permitir: false);
                permitir.Content.Should().Be("Permitir 1 de 2");
                card.Marcar(0, permitir: false);
                permitir.Content.Should().Be("Permitir os 2", "clicar de novo tira a marca");

                card.Marcar(1, permitir: false);
                permitir.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                card.Resposta.IsCompleted.Should().BeTrue();
                card.Resposta.Result.Allowed.Should().BeTrue("um dos dois segue permitido");
                juntado.Recusados.Should().Equal(1);
            });
        }

        [Fact]
        public void NoCartao_TodosRecusados_ViraRecusa()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var juntado = new Services.Tools.DocTool().JuntarNoCartao(new[] { Item(@"C:\t\a.xlsx"), Item(@"C:\t\b.xlsx") });
                var card = new ConfirmCardView();
                card.Preencher(juntado);

                card.Marcar(0, permitir: false);
                card.Marcar(1, permitir: false);
                var permitir = (Button)card.FindName("PermitirButton");
                permitir.Content.Should().Be("Confirmar a recusa");
                permitir.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                card.Resposta.Result.Allowed.Should().BeFalse();
            });
        }
    }
}
