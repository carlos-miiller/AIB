using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AIB.Services;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A página Cofre das Configurações (mockup "Cofre de aliases do AIB", 20/10/2026): guardar,
    /// trocar o valor e apagar, sem o valor voltar à tela.
    /// </summary>
    public class PaginaDoCofreTests
    {
        private static (PaginaDoCofre pagina, Cofre cofre) Nova()
        {
            var cofre = new Cofre(Path.Combine(Path.GetTempPath(), "AIB_PaginaCofre_" + Guid.NewGuid().ToString("N")));
            var pagina = new PaginaDoCofre { Cofre = cofre };
            pagina.Atualizar();
            return (pagina, cofre);
        }

        private static T Achar<T>(PaginaDoCofre p, string nome) where T : class => (T)p.FindName(nome);

        private static void Clicar(PaginaDoCofre p, string nome) =>
            Achar<Button>(p, nome).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

        private static StackPanel Lista(PaginaDoCofre p) => Achar<StackPanel>(p, "Lista");

        [Fact]
        public void ColarWebhook_SugereOHost_EGuardar_ListaSemOValor_ELimpaOFormulario()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (p, cofre) = Nova();

                // O formulário nasce fechado; o botão tracejado o abre no lugar dele, como o
                // "Adicionar conta" da página E-mail (pedido do usuário, 20/10/2026).
                Achar<Border>(p, "Formulario").Visibility.Should().Be(Visibility.Collapsed);
                Clicar(p, "AdicionarButton");
                Achar<Border>(p, "Formulario").Visibility.Should().Be(Visibility.Visible);
                Achar<Button>(p, "AdicionarButton").Visibility.Should().Be(Visibility.Collapsed);

                Achar<TextBox>(p, "NomeBox").Text = "bitrix";
                Achar<PasswordBox>(p, "ValorBox").Password = "https://cpaps.bitrix24.com.br/rest/1/k3x9q2v7m1p8z4t6/";
                Achar<TextBox>(p, "HostsBox").Text.Should().Be("cpaps.bitrix24.com.br");
                Achar<TextBlock>(p, "VariavelText").Text.Should().Contain("$env:COFRE_BITRIX");
                Achar<TextBox>(p, "DescricaoBox").Text = "Webhook REST do Bitrix";

                Clicar(p, "GuardarButton");

                cofre.Achar("bitrix")!.Hosts.Should().Equal("cpaps.bitrix24.com.br");
                Achar<PasswordBox>(p, "ValorBox").Password.Should().BeEmpty();
                Achar<Border>(p, "Formulario").Visibility.Should().Be(Visibility.Collapsed, "guardou: o botão volta");
                Achar<Button>(p, "AdicionarButton").Visibility.Should().Be(Visibility.Visible);
                Lista(p).Children.Count.Should().Be(1);
                Textos(Lista(p)).Should().NotContain(t => t.Contains("k3x9"));
            });
        }

        [Fact]
        public void SemDescricao_NaoGuarda_EMostraOErro()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (p, cofre) = Nova();

                Achar<TextBox>(p, "NomeBox").Text = "github";
                Achar<PasswordBox>(p, "ValorBox").Password = "ghp_abcdefghij";
                Clicar(p, "GuardarButton");

                cofre.Listar().Should().BeEmpty();
                Achar<TextBlock>(p, "ErroText").Visibility.Should().Be(Visibility.Visible);
                Achar<PasswordBox>(p, "ValorBox").Password.Should().Be("ghp_abcdefghij", "o erro não apaga o que foi digitado");
            });
        }

        [Fact]
        public void Apagar_PedeDoisCliques()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (p, cofre) = Nova();
                cofre.Guardar("github", "ghp_abcdefghij", "token", new[] { "api.github.com" });
                p.Atualizar();

                Button Apagar() => Botoes(Lista(p)).Single(b => ((string)b.Content).Contains("pagar"));

                Apagar().RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                cofre.Listar().Should().HaveCount(1);
                ((string)Apagar().Content).Should().StartWith("Confirmar");

                Apagar().RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                cofre.Listar().Should().BeEmpty();
            });
        }

        [Fact]
        public void Editar_TravaONome_TrocaOValor_EMantemDescricao()
        {
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (p, cofre) = Nova();
                cofre.Guardar("github", "ghp_abcdefghij", "token do GitHub", new[] { "api.github.com" });
                p.Atualizar();

                Botoes(Lista(p)).Single(b => (string)b.Content == "Editar")
                    .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

                Achar<TextBox>(p, "NomeBox").IsReadOnly.Should().BeTrue();
                Achar<PasswordBox>(p, "ValorBox").Password = "ghp_novovalor123";
                Clicar(p, "GuardarButton");

                cofre.Achar("github")!.Descricao.Should().Be("token do GitHub");
                cofre.Redigir("x ghp_novovalor123").Should().Be("x [cofre: github]");
            });
        }

        [Fact]
        public void SemHosts_AMetaDizQualquerEndereco()
        {
            var a = new Alias("sankhya", "senha", Array.Empty<string>(), DateTime.Now, null, 0);
            PaginaDoCofre.Meta(a).Should().Be("qualquer endereço · shell e habilidades · nunca usado");
            PaginaDoCofre.Meta(a with { Shell = false }).Should().Contain("só habilidades");
        }

        [Fact]
        public void Editar_SemValor_MantemOSegredo_EDesligaOShell()
        {
            // Pedido do usuário (20/10/2026): "inclua uma flag em cada chave para 'Permite uso no
            // shell'". Desligar não pode exigir colar o segredo de novo.
            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();
                var (p, cofre) = Nova();
                cofre.Guardar("bitrix", "https://cpaps.bitrix24.com.br/rest/1/k3x9q2v7m1p8z4t6/", "Webhook", new[] { "cpaps.bitrix24.com.br" });
                p.Atualizar();

                Botoes(Lista(p)).Single(b => (string)b.Content == "Editar")
                    .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Achar<CheckBox>(p, "ShellCheck").IsChecked.Should().BeTrue();

                Achar<CheckBox>(p, "ShellCheck").IsChecked = false;
                Clicar(p, "GuardarButton");

                cofre.Achar("bitrix")!.Shell.Should().BeFalse();
                cofre.Redigir("https://cpaps.bitrix24.com.br/rest/1/k3x9q2v7m1p8z4t6/").Should().Be("[cofre: bitrix]",
                    "o valor guardado continua o mesmo");
            });
        }

        private static System.Collections.Generic.IEnumerable<T> Todos<T>(DependencyObject raiz) where T : DependencyObject
        {
            foreach (object filho in LogicalTreeHelper.GetChildren(raiz))
            {
                if (filho is not DependencyObject d) continue;
                if (d is T t) yield return t;
                foreach (var n in Todos<T>(d)) yield return n;
            }
        }

        private static System.Collections.Generic.IEnumerable<string> Textos(DependencyObject raiz) =>
            Todos<TextBlock>(raiz).Select(t => t.Text);

        private static System.Collections.Generic.IEnumerable<Button> Botoes(DependencyObject raiz) => Todos<Button>(raiz);
    }
}
