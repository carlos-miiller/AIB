using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AIB.Services;
using AIB.Services.Documentos;
using AIB.Views;
using FluentAssertions;

namespace AIB.Tests
{
    /// <summary>
    /// O cartão de gravação de documento — mockup "Edição de documentos do AIB" (19/10/2026).
    /// </summary>
    public class CartaoDeDocumentoTests
    {
        private static CommandConfirmationContext Contexto(PlanoDeGravacao plano) => new()
        {
            Tool = Ferramentas.Documento,
            Command = (plano.Criar ? "CRIAR " : "EDITAR ") + plano.Arquivo,
            ScriptBody = plano.EmTexto(),
            Plano = plano,
            SemSempre = true
        };

        [Fact]
        public void AEdicao_MostraATabela_OEfeito_EACopia_EmVermelho()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var plano = new PlanoDeGravacao
                {
                    Arquivo = @"C:\Users\Carlo\Documents\Custos TI 2026.xlsx",
                    Titulo = "Gravar 2 células nesta planilha?",
                    Consequencia = "Os valores atuais são substituídos.",
                    Resumo = "aba \"Outubro\" · 2 células",
                    Mudancas = new List<Mudanca>
                    {
                        new(TipoDeMudanca.Valor, "C14", "1.180,00", "1.290,00"),
                        new(TipoDeMudanca.Valor, "C15", null, "640,00")
                    },
                    Efeitos = new List<string> { "C42 (Total): 12.400,00 → 13.150,00" },
                    Copia = "Custos TI 2026.antes-AIB.xlsx"
                };

                var card = new ConfirmCardView();
                card.Preencher(Contexto(plano));
                card.ParaFlutuante();

                ((TextBlock)card.FindName("TituloText")).Text.Should().Be("Gravar 2 células nesta planilha?");
                ((FrameworkElement)card.FindName("PlanoPanel")).Visibility.Should().Be(Visibility.Visible);
                ((FrameworkElement)card.FindName("PreviaBorder")).Visibility.Should().Be(Visibility.Collapsed,
                    "a tabela substitui a prévia em texto");

                var tabela = (Grid)card.FindName("PlanoTabela");
                tabela.RowDefinitions.Count.Should().Be(3, "cabeçalho e duas mudanças");
                tabela.Children.OfType<TextBlock>().Select(t => t.Text).Should().Contain(new[] { "C14", "1.290,00", "vazia" });

                ((Panel)card.FindName("PlanoEfeitos")).Children.Count.Should().Be(2);
                ((TextBlock)card.FindName("PlanoCopiaText")).Text.Should().Contain("Custos TI 2026.antes-AIB.xlsx");
                card.Pintado.Should().Be(ConfirmCardView.Risco.Destrutivo);
                ((TextBlock)card.FindName("PillTexto")).Text.Should().Be("Edita o arquivo original");
            });
        }

        [Fact]
        public void AEdicaoDeWord_ELaranja_PorqueViraControleDeAlteracoes()
        {
            // Decisão do usuário (19/10/2026): a edição de Word entra como revisão, que se
            // rejeita no próprio Word — não é a ação sem volta do vermelho.
            ConfirmCardView.RiscoDe(Ferramentas.Documento, @"EDITAR C:\docs\Proposta.docx", false)
                .Should().Be((ConfirmCardView.Risco.Cuidado, "Sugere alterações"));
            ConfirmCardView.RiscoDe(Ferramentas.Documento, @"EDITAR C:\docs\Custos.xlsx", false).Risco
                .Should().Be(ConfirmCardView.Risco.Destrutivo);
        }

        [Fact]
        public void ACriacao_ELaranja_EMostraAPrevia()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var plano = new PlanoDeGravacao
                {
                    Arquivo = @"C:\temp\Chamados.xlsx",
                    Criar = true,
                    Titulo = "Criar esta planilha?",
                    Previa = PreviaDeArquivo.De(new[] { "Chamados", "Resumo" },
                        new[] { new[] { "Nº", "Status" }, new[] { "4821", "Aberto" } })
                };

                var card = new ConfirmCardView();
                card.Preencher(Contexto(plano));
                card.ParaFlutuante();

                card.Pintado.Should().Be(ConfirmCardView.Risco.Cuidado);
                ((TextBlock)card.FindName("PillTexto")).Text.Should().Be("Cria um arquivo");
                ((FrameworkElement)card.FindName("PlanoCopiaText")).Visibility.Should().Be(Visibility.Collapsed);
                ((FrameworkElement)card.FindName("PlanoTabelaBorder")).Visibility.Should().Be(Visibility.Visible);
            });
        }
    }
}
