using System.Windows;
using System.Windows.Controls;
using AIB.Services;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O que o card de confirmação DIZ, ferramenta por ferramenta.
    /// <para>
    /// O WriteFileTool e o EditFileTool montavam a prévia do conteúdo e o antes e depois em
    /// <c>ScriptBody</c>, e nenhuma view lia o campo: o usuário autorizava uma gravação vendo só
    /// o caminho. E o texto do write dizia "o conteúdo atual será substituído" também para um
    /// arquivo que ainda não existia.
    /// </para>
    /// </summary>
    public class ConfirmCardViewTests
    {
        private static ConfirmCardView Montar(CommandConfirmationContext contexto)
        {
            var card = new ConfirmCardView();
            card.Preencher(contexto);
            return card;
        }

        private static string Texto(ConfirmCardView card, string nome) =>
            ((TextBlock)card.FindName(nome)).Text;

        private static Visibility Previa(ConfirmCardView card) =>
            ((Border)card.FindName("PreviaBorder")).Visibility;

        [Fact]
        public void Write_QueCRIA_NaoFalaEmSubstituir_EMostraOConteudo()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var card = Montar(new CommandConfirmationContext
                {
                    Tool = Ferramentas.Gravar,
                    Command = @"CRIAR C:\temp\novo.txt",
                    ScriptBody = "linha um\nlinha dois"
                });

                Texto(card, "TituloText").Should().Be("Criar este arquivo?");
                Texto(card, "ConsequenciaText").Should().NotContain("substituído",
                    "o arquivo ainda não existe: não há conteúdo atual a substituir");
                Previa(card).Should().Be(Visibility.Visible);
                Texto(card, "PreviaText").Should().Be("linha um\nlinha dois");
            });
        }

        [Fact]
        public void Write_QueSOBRESCREVE_AvisaQueSubstitui()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var card = Montar(new CommandConfirmationContext
                {
                    Tool = Ferramentas.Gravar,
                    Command = @"SOBRESCREVER C:\temp\velho.txt",
                    ScriptBody = "novo"
                });

                Texto(card, "TituloText").Should().Be("Sobrescrever este arquivo?");
                Texto(card, "ConsequenciaText").Should().Contain("substituído");
            });
        }

        [Fact]
        public void Edit_TemTextoProprio_EMostraOAntesEODepois()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var card = Montar(new CommandConfirmationContext
                {
                    Tool = Ferramentas.Editar,
                    Command = @"EDITAR C:\temp\a.txt",
                    ScriptBody = "- velho\n+ novo"
                });

                Texto(card, "TituloText").Should().Be("Editar este arquivo?");
                Texto(card, "ConsequenciaText").Should().NotBe(
                    "Esta ação altera o seu sistema e não pode ser desfeita pelo AIB.",
                    "o texto genérico não diz o que a edição faz");
                Texto(card, "PreviaText").Should().Contain("- velho").And.Contain("+ novo");
            });
        }

        [Fact]
        public void Skill_TemTextoProprio()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var card = Montar(new CommandConfirmationContext
                {
                    Tool = Ferramentas.Habilidade,
                    Command = @"C:\skills\planilha\s.ps1 -Path a.xlsx"
                });

                Texto(card, "TituloText").Should().Be("Executar esta habilidade?");
                Texto(card, "ConsequenciaText").Should().Contain("script");
            });
        }

        [Fact]
        public void SemPrevia_OBlocoNaoAparece()
        {
            // Um bloco vazio no card é ruído — e o shell não tem prévia: o comando já é o alvo.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var card = Montar(new CommandConfirmationContext
                {
                    Tool = Ferramentas.Shell,
                    Command = "Get-Process"
                });

                Previa(card).Should().Be(Visibility.Collapsed);
            });
        }
    }
}
